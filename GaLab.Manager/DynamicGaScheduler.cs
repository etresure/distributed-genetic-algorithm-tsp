using System.Diagnostics;
using GaLab.Shared;

namespace GaLab.Manager;

internal sealed class DynamicGaScheduler(
    ServiceCommunication communication,
    Uri[] services,
    SolveRequest baseRequest,
    TimeSpan interval,
    TimeSpan idleLimit,
    int jobsPerService,
    IProgress<ServiceSnapshot>? progress,
    IEnumerable<string> initialWarnings)
{
    private sealed class WorkItem(int number, SolveRequest request)
    {
        public int Number { get; } = number;
        public SolveRequest Request { get; } = request;
        public Dictionary<Uri, int> Attempts { get; } = [];
    }

    private sealed class ServiceConnection(Uri url)
    {
        public Uri Url { get; } = url;
        public SemaphoreSlim Wake { get; } = new(0, 1);
        public Task? RunTask { get; set; }
        public bool Enabled { get; set; } = true;
        public ServiceRunState State { get; set; } = ServiceRunState.Reconnecting;
        public int CompletedJobs { get; set; }
    }

    private const int MaxAttemptsPerService = 2;
    private readonly object gate = new();
    private readonly Dictionary<Uri, ServiceConnection> connections = services.ToDictionary(
        url => url, url => new ServiceConnection(url));
    private readonly Queue<WorkItem> pending = new();
    private readonly List<string> warnings = [.. initialWarnings];
    private readonly TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int jobCount = (int)Math.Min(baseRequest.PopulationSize / 2L, (long)services.Length * jobsPerService);
    private CancellationTokenSource? execution;
    private int remaining;
    private int inFlight;
    private int permanentlyFailed;
    private long lastMovement = Stopwatch.GetTimestamp();
    private SolveResponse? best;
    private bool finished;

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (gate)
                return [.. warnings];
        }
    }

    public void UpdateServices(IReadOnlyCollection<Uri> updated)
    {
        List<ServiceSnapshot> disconnected = [];
        lock (gate)
        {
            if (finished || execution is null)
                throw new InvalidOperationException("Нет выполняющегося вычисления.");

            HashSet<Uri> selected = [.. updated];
            bool modified = false;
            foreach (ServiceConnection connection in connections.Values)
            {
                bool enabled = selected.Contains(connection.Url);
                if (connection.Enabled == enabled)
                    continue;

                modified = true;
                connection.Enabled = enabled;
                if (enabled && connection.Wake.CurrentCount == 0)
                    connection.Wake.Release();
                else
                {
                    connection.State = ServiceRunState.Disconnected;
                    disconnected.Add(new ServiceSnapshot(connection.Url.ToString().TrimEnd('/'),
                        ServiceRunState.Disconnected, null, 0, 0, connection.CompletedJobs));
                }
            }

            foreach (Uri url in selected)
                if (connections.TryAdd(url, new ServiceConnection(url)))
                {
                    modified = true;
                    Start(connections[url]);
                }

            if (modified)
                lastMovement = Stopwatch.GetTimestamp();
        }
        foreach (ServiceSnapshot snapshot in disconnected)
            progress?.Report(snapshot);
    }

    private void Start(ServiceConnection connection)
    {
        connection.RunTask = Task.Run(async () =>
        {
            try
            {
                await RunServiceAsync(connection, execution!.Token);
            }
            catch (OperationCanceledException) when (execution!.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
            finally
            {
                ServiceSnapshot snapshot;
                lock (gate)
                {
                    connection.State = connection.Enabled ? ServiceRunState.Finished : ServiceRunState.Disconnected;
                    snapshot = new ServiceSnapshot(connection.Url.ToString().TrimEnd('/'), connection.State,
                        null, 0, 0, connection.CompletedJobs, "Обработка завершена");
                }
                progress?.Report(snapshot);
            }
        });
    }

    private WorkItem? TakeJob(ServiceConnection connection)
    {
        lock (gate)
        {
            if (!connection.Enabled)
                return null;

            int count = pending.Count;
            for (int i = 0; i < count; i++)
            {
                WorkItem job = pending.Dequeue();
                int attempts = job.Attempts.GetValueOrDefault(connection.Url);
                bool preferOther = attempts > 0 && connections.Values.Any(other =>
                    other != connection && other.Enabled &&
                    other.State is ServiceRunState.Ready or ServiceRunState.Running &&
                    job.Attempts.GetValueOrDefault(other.Url) < attempts);
                if (attempts < MaxAttemptsPerService && !preferOther)
                {
                    job.Attempts[connection.Url] = attempts + 1;
                    connection.State = ServiceRunState.Running;
                    inFlight++;
                    return job;
                }
                pending.Enqueue(job);
            }
            return null;
        }
    }

    private bool FinishJob(ServiceConnection connection, WorkItem job, MonitoredServiceCallResult monitored)
    {
        lock (gate)
        {
            ServiceCallResult? result = monitored.Result;
            bool succeeded = !monitored.ConnectionLost &&
                result?.Response?.Generation == job.Request.Generations;
            if (succeeded)
            {
                SolveResponse response = result!.Response!;
                if (best is null || response.Length < best.Length)
                    best = response;
                connection.CompletedJobs++;
                remaining--;
            }
            else
            {
                if (result?.Warning is { } warning)
                    warnings.Add(warning);
                else if (!monitored.ConnectionLost && result?.Response is not null)
                    warnings.Add($"Задание {job.Number}: сервис вернул неполное число поколений.");

                ServiceConnection[] active = [.. connections.Values.Where(item => item.Enabled)];
                if (active.Length > 0 && active.All(item =>
                    job.Attempts.GetValueOrDefault(item.Url) >= MaxAttemptsPerService))
                {
                    permanentlyFailed++;
                    remaining--;
                    warnings.Add($"Задание {job.Number}: не выполнено после {job.Attempts.Values.Sum()} попыток.");
                }
                else
                    pending.Enqueue(job);
            }

            connection.State = !connection.Enabled ? ServiceRunState.Disconnected :
                succeeded ? ServiceRunState.Ready : ServiceRunState.Reconnecting;
            inFlight--;
            lastMovement = Stopwatch.GetTimestamp();
            if (remaining == 0)
                completed.TrySetResult(true);
            return succeeded;
        }
    }

    private async Task RunServiceAsync(ServiceConnection connection, CancellationToken cancellationToken)
    {
        Uri url = connection.Url;
        string name = url.ToString().TrimEnd('/');
        void Report(ServiceRunState state, WorkItem? job = null, int generation = 0, string? detail = null)
        {
            ServiceSnapshot snapshot;
            lock (gate)
                snapshot = new ServiceSnapshot(name, connection.Enabled ? state : ServiceRunState.Disconnected,
                    job?.Request.JobId, generation, job?.Request.Generations ?? 0,
                    connection.CompletedJobs, detail);
            progress?.Report(snapshot);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            bool enabled;
            lock (gate)
            {
                if (remaining == 0)
                    break;
                enabled = connection.Enabled;
            }
            if (!enabled)
            {
                await connection.Wake.WaitAsync(cancellationToken);
                continue;
            }

            ServiceStatusResponse? status = await communication.GetStatusAsync(url, interval, cancellationToken);
            ServiceRunState state;
            lock (gate)
            {
                state = !connection.Enabled ? ServiceRunState.Disconnected : status is null
                    ? ServiceRunState.Reconnecting : status.ActiveJobs.Length == 0
                    ? ServiceRunState.Ready : ServiceRunState.Busy;
                connection.State = state;
            }
            if (state == ServiceRunState.Disconnected)
                continue;
            if (status is null)
                Report(ServiceRunState.Reconnecting, detail: "Сервис не отвечает");
            else if (state == ServiceRunState.Busy)
                Report(ServiceRunState.Busy, detail: "Выполняет другое задание");
            else
            {
                Report(ServiceRunState.Ready, detail: "Ожидает задание");
                WorkItem? job = TakeJob(connection);
                if (job is not null)
                {
                    MonitoredServiceCallResult result = await communication.SolveMonitoredAsync(
                        url, status.InstanceId, job.Request, job.Number, interval, cancellationToken,
                        (generation, detail) => Report(ServiceRunState.Running, job, generation, detail),
                        detail => Report(ServiceRunState.Reconnecting, job, detail: detail));
                    bool succeeded = FinishJob(connection, job, result);
                    Report(succeeded ? ServiceRunState.Ready : ServiceRunState.Reconnecting,
                        job, succeeded ? job.Request.Generations : 0,
                        succeeded ? "Задание завершено" : "Задание возвращено в очередь");
                    continue;
                }
            }
            await Task.Delay(interval, cancellationToken);
        }
    }

    private void CheckProgress()
    {
        lock (gate)
        {
            if (completed.Task.IsCompleted || inFlight != 0 ||
                Stopwatch.GetElapsedTime(lastMovement) < idleLimit)
                return;

            foreach (ServiceConnection connection in connections.Values.Where(item => item.Enabled))
            {
                string name = connection.Url.ToString().TrimEnd('/');
                warnings.Add(connection.State == ServiceRunState.Reconnecting
                    ? $"Сервис {name}: недоступен; истёк период без продвижения."
                    : $"Сервис {name}: нет продвижения в течение {idleLimit.TotalSeconds:G} с.");
            }
            if (connections.Values.Any(connection => connection.Enabled))
                completed.TrySetResult(true);
        }
    }

    public async Task<SolveResponse?> RunAsync(CancellationToken cancellationToken)
    {
        int share = baseRequest.PopulationSize / jobCount;
        int remainder = baseRequest.PopulationSize % jobCount;
        string batchId = Guid.NewGuid().ToString("N");
        for (int i = 0; i < jobCount; i++)
            pending.Enqueue(new WorkItem(i, baseRequest with
            {
                JobId = $"{batchId}-{i}",
                PopulationSize = share + (i < remainder ? 1 : 0),
                Seed = baseRequest.Seed is int seed ? unchecked(seed + i) : Random.Shared.Next()
            }));
        remaining = jobCount;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (gate)
        {
            execution = lifetime;
            foreach (ServiceConnection connection in connections.Values)
                Start(connection);
        }
        try
        {
            while (!completed.Task.IsCompleted)
            {
                await Task.WhenAny(completed.Task, Task.Delay(idleLimit, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                CheckProgress();
            }
            await completed.Task;
        }
        finally
        {
            lock (gate)
                finished = true;
            await lifetime.CancelAsync();
            Task[] tasks;
            lock (gate)
                tasks = [.. connections.Values.Select(connection => connection.RunTask).OfType<Task>()];
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            lock (gate)
            {
                if (remaining > 0)
                    warnings.Add($"Не завершено заданий: {remaining} из {jobCount}.");
                if (permanentlyFailed > 0)
                    warnings.Add($"Не удалось выполнить заданий: {permanentlyFailed} из {jobCount}.");
            }
            foreach (ServiceConnection connection in connections.Values)
                connection.Wake.Dispose();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return best;
    }
}
