using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using GaLab.Manager;
using GaLab.Shared;

namespace GaLab.Tests;

public sealed class DynamicSchedulingTests
{
    [Fact]
    public async Task FastServiceTakesMoreJobs_AndTotalPopulationIsPreserved()
    {
        var assignments = new ConcurrentBag<(string Host, SolveRequest Request)>();
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(cancellationToken: token))!;
            string host = message.RequestUri!.Host;
            assignments.Add((host, request));
            await Task.Delay(host == "slow" ? 160 : 10, token);
            return Reply(request);
        });

        var manager = new GaManagerClient(client, TimeSpan.FromSeconds(2));
        SolveRequest request = TspSolverTests.Request() with { PopulationSize = 40, Generations = 2 };
        SolveResponse best = await manager.SolveDynamicallyAsync(
            ["http://slow:5001", "http://fast:5002"], request,
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(20), jobsPerService: 4);

        Assert.Equal(80, best.Length);
        Assert.Equal(40, assignments.Sum(x => x.Request.PopulationSize));
        Assert.True(assignments.Count(x => x.Host == "fast") > assignments.Count(x => x.Host == "slow"));
        Assert.Equal(assignments.Count, assignments.Select(x => x.Request.JobId).Distinct().Count());
        Assert.Empty(manager.LastWarnings);
    }

    [Fact]
    public async Task AllConnectedServicesFinishAfterSuccessfulComputation()
    {
        var snapshots = new ConcurrentQueue<ServiceSnapshot>();
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            await Task.Delay(30, token);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        await manager.SolveDynamicallyAsync(
            ["http://first:5001", "http://second:5002"],
            TspSolverTests.Request() with { PopulationSize = 8, Generations = 2 },
            CancellationToken.None, new InlineProgress(snapshots.Enqueue),
            pollInterval: TimeSpan.FromMilliseconds(10));

        foreach (string url in new[] { "http://first:5001", "http://second:5002" })
        {
            ServiceSnapshot[] reports = [.. snapshots.Where(snapshot => snapshot.ServiceUrl == url)];
            Assert.Single(reports.Where(snapshot => snapshot.State == ServiceRunState.Finished));
            Assert.Equal(ServiceRunState.Finished, reports[^1].State);
        }
    }

    [Fact]
    public async Task FailedJobIsDispatchedToAnotherService()
    {
        var attempts = new ConcurrentBag<(string Host, string? JobId)>();
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(cancellationToken: token))!;
            string host = message.RequestUri!.Host;
            attempts.Add((host, request.JobId));
            if (host == "broken")
                throw new HttpRequestException("соединение с сервисом разорвано");
            await Task.Delay(40, token);
            return Reply(request);
        });
        var manager = new GaManagerClient(client, TimeSpan.FromSeconds(2));
        await manager.SolveDynamicallyAsync(
            ["http://broken:5001", "http://healthy:5002"],
            TspSolverTests.Request() with { PopulationSize = 16, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(20), jobsPerService: 4);

        Assert.NotEmpty(attempts.Where(a => a.Host == "broken"));
        Assert.Contains(attempts.Where(a => a.Host == "healthy"),
            a => attempts.Any(b => b.Host == "broken" && b.JobId == a.JobId));
        Assert.All(attempts.Where(a => a.Host == "broken").GroupBy(a => a.JobId),
            group => Assert.Single(group));
        Assert.DoesNotContain(manager.LastWarnings, w => w.Contains("Не завершено заданий"));
    }

    [Fact]
    public async Task ManagerReportsLiveGenerationProgress()
    {
        var snapshots = new ConcurrentBag<ServiceSnapshot>();
        string? activeJob = null;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ServiceStatusResponse("instance",
                        Volatile.Read(ref activeJob) is not { } id ? [] :
                            [new JobProgress(id, 1, 2, 80)]))
                };
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(cancellationToken: token))!;
            Volatile.Write(ref activeJob, request.JobId);
            await Task.Delay(120, token);
            Volatile.Write(ref activeJob, null);
            return Reply(request);
        });
        var manager = new GaManagerClient(client);
        await manager.SolveDynamicallyAsync(["http://worker:5001"],
            TspSolverTests.Request() with { PopulationSize = 4, Generations = 2 },
            CancellationToken.None, new InlineProgress(snapshots.Add),
            pollInterval: TimeSpan.FromMilliseconds(20));
        Assert.Contains(snapshots, s => s.State == ServiceRunState.Running && s.Generation == 1);
    }

    [Fact]
    public async Task TransientHeartbeatFailures_DoNotCancelSuccessfulComputation()
    {
        int started = 0;
        int attempts = 0;
        int missed = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
            {
                if (Volatile.Read(ref started) != 0 && Interlocked.Increment(ref missed) <= 6)
                    throw new HttpRequestException("временная недоступность сервиса мониторинга");
                return Status();
            }
            Interlocked.Increment(ref attempts);
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            Volatile.Write(ref started, 1);
            await Task.Delay(180, token);
            return Reply(request);
        });
        var manager = new GaManagerClient(client, TimeSpan.FromSeconds(2));
        SolveResponse result = await manager.SolveDynamicallyAsync(
            ["http://worker:5001"],
            TspSolverTests.Request() with { PopulationSize = 4, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10),
            reconnectWindow: TimeSpan.FromMilliseconds(50));

        Assert.Equal(80, result.Length);
        Assert.Equal(1, attempts);
        Assert.True(missed >= 3);
        Assert.DoesNotContain(manager.LastWarnings, w => w.Contains("Не завершено заданий"));
    }

    [Fact]
    public async Task DefaultPartitioning_KeepsOneIndependentSearchPerService()
    {
        var jobs = new ConcurrentBag<SolveRequest>();
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            jobs.Add(request);
            return Reply(request);
        });
        var manager = new GaManagerClient(client);
        await manager.SolveDynamicallyAsync(["http://first:5001", "http://second:5002"],
            TspSolverTests.Request() with { PopulationSize = 40, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Equal(20, job.PopulationSize));
    }

    [Fact]
    public async Task RecoveredService_ReceivesWorkEvenAfterOldGracePeriod()
    {
        long start = Stopwatch.GetTimestamp();
        int recoveredJobs = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            string host = message.RequestUri!.Host;
            if (message.Method == HttpMethod.Get)
            {
                if (host == "recovering" && Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(160))
                    throw new HttpRequestException("сервис временно недоступен");
                return Status();
            }
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            if (host == "recovering")
                Interlocked.Increment(ref recoveredJobs);
            else
                await Task.Delay(240, token);
            return Reply(request);
        });
        var manager = new GaManagerClient(client, TimeSpan.FromSeconds(2));
        await manager.SolveDynamicallyAsync(["http://slow:5001", "http://recovering:5002"],
            TspSolverTests.Request() with { PopulationSize = 32, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10),
            reconnectWindow: TimeSpan.FromMilliseconds(70), jobsPerService: 4);

        Assert.True(recoveredJobs > 0);
        Assert.DoesNotContain(manager.LastWarnings, w => w.Contains("Не завершено заданий"));
    }

    [Fact]
    public async Task BusyServiceIsNotOversubscribed()
    {
        var snapshots = new ConcurrentBag<ServiceSnapshot>();
        using HttpClient client = FakeClient((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ServiceStatusResponse("instance",
                    [new JobProgress("external", 1, 2, 80)]))
            }));
        var manager = new GaManagerClient(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.SolveDynamicallyAsync(["http://busy:5001"], TspSolverTests.Request(),
                cancellation.Token, new InlineProgress(snapshots.Add), pollInterval: TimeSpan.FromMilliseconds(20)));
        Assert.Contains(snapshots, s => s.State == ServiceRunState.Busy);
    }

    [Fact]
    public async Task OfflineServicesProduceBoundedFailure()
    {
        var snapshots = new ConcurrentQueue<ServiceSnapshot>();
        using HttpClient client = FakeClient((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("нет соединения")));
        var manager = new GaManagerClient(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SolveDynamicallyAsync(
            ["http://offline:5001"], TspSolverTests.Request(), CancellationToken.None,
            new InlineProgress(snapshots.Enqueue),
            pollInterval: TimeSpan.FromMilliseconds(20), reconnectWindow: TimeSpan.FromMilliseconds(90)));
        Assert.Contains(manager.LastWarnings, warning => warning.Contains("недоступен"));
        Assert.Single(snapshots.Where(snapshot => snapshot.State == ServiceRunState.Finished));
        Assert.Equal(ServiceRunState.Finished, snapshots.Last().State);
    }

    [Fact]
    public async Task DuplicateServiceAddresses_DoNotExecuteJobsConcurrentlyOnOneService()
    {
        int active = 0;
        int maximum = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();

            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            int count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(Volatile.Read(ref maximum), count));
            try
            {
                await Task.Delay(35, token);
                return Reply(request);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var manager = new GaManagerClient(client);
        await manager.SolveDynamicallyAsync(
            ["http://worker:5001", "http://worker:5001"],
            TspSolverTests.Request() with { PopulationSize = 8, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Equal(1, maximum);
        Assert.Contains(manager.LastWarnings, warning => warning.Contains("Повторяющиеся"));
    }

    [Fact]
    public async Task UnavailableServiceThatRecoversWithinGrace_ReceivesWork()
    {
        int polls = 0;
        int assignments = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref polls) <= 2)
                    throw new HttpRequestException("временный сбой");
                return Status();
            }

            Interlocked.Increment(ref assignments);
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            return Reply(request);
        });
        var manager = new GaManagerClient(client);
        SolveResponse result = await manager.SolveDynamicallyAsync(
            ["http://recovering:5001"],
            TspSolverTests.Request() with { PopulationSize = 8, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(15),
            reconnectWindow: TimeSpan.FromSeconds(1));

        Assert.Equal(80, result.Length);
        Assert.True(assignments > 0);
        Assert.DoesNotContain(manager.LastWarnings, warning => warning.Contains("недоступен"));
    }

    [Fact]
    public async Task EarlyResponse_IsNotCountedAsACompletedJob()
    {
        int attempts = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            Interlocked.Increment(ref attempts);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SolveResponse(
                    [0, 1, 3, 2], 80, 0,
                    [new GenerationPoint(0, 80)]))
            };
        });
        var manager = new GaManagerClient(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SolveDynamicallyAsync(
            ["http://early:5001"],
            TspSolverTests.Request() with { PopulationSize = 4, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10)));

        Assert.True(attempts >= 2);
        Assert.Contains(manager.LastWarnings, warning => warning.Contains("неполное число поколений"));
        Assert.Contains(manager.LastWarnings, warning => warning.Contains("Не удалось выполнить заданий"));
    }

    [Fact]
    public async Task ServiceDisconnectAfterAssignment_ReportsAllOutstandingJobs()
    {
        int jobStarted = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
            {
                if (Volatile.Read(ref jobStarted) != 0)
                    throw new HttpRequestException("сервис недоступен");
                return Status();
            }
            Volatile.Write(ref jobStarted, 1);
            await Task.Delay(15, token);
            throw new HttpRequestException("соединение закрыто");
        });
        var manager = new GaManagerClient(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SolveDynamicallyAsync(
            ["http://lost:5001"],
            TspSolverTests.Request() with { PopulationSize = 6, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10),
            reconnectWindow: TimeSpan.FromMilliseconds(70)));

        Assert.Contains(manager.LastWarnings, warning => warning.Contains("Не завершено заданий"));
    }

    [Fact]
    public async Task ServicesCanBeReplacedWhileAJobIsRunning()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var assignments = new ConcurrentBag<(string Host, SolveRequest Request)>();
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            string host = message.RequestUri!.Host;
            assignments.Add((host, request));
            if (host == "first")
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(token);
            }
            else
                replacementStarted.TrySetResult(true);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://first:5001"],
            TspSolverTests.Request() with { PopulationSize = 32, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10), jobsPerService: 4);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            manager.UpdateServices(["http://replacement:5002"]);
            await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        SolveResponse result = await computation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(80, result.Length);
        Assert.Equal(1, assignments.Count(item => item.Host == "first"));
        Assert.Equal(3, assignments.Count(item => item.Host == "replacement"));
        Assert.Equal(32, assignments.Sum(item => item.Request.PopulationSize));
        Assert.Empty(manager.LastWarnings);
    }

    [Fact]
    public async Task RemovingAllServicesPausesPendingJobsUntilAnotherServiceIsAdded()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int replacementJobs = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            if (message.RequestUri!.Host == "first")
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(token);
            }
            else
                Interlocked.Increment(ref replacementJobs);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://first:5001"],
            TspSolverTests.Request() with { PopulationSize = 16, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(10),
            reconnectWindow: TimeSpan.FromMilliseconds(60), jobsPerService: 4);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            manager.UpdateServices([]);
            releaseFirst.TrySetResult(true);
            await Task.Delay(150);
            Assert.False(computation.IsCompleted);
            manager.UpdateServices(["http://replacement:5002"]);
            await computation.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }

        Assert.Equal(3, replacementJobs);
        Assert.Empty(manager.LastWarnings);
    }

    [Fact]
    public async Task NewServiceStartsWithoutWaitingForTheSchedulingPollInterval()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();

            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            if (message.RequestUri!.Host == "first")
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(token);
            }
            else
                secondStarted.TrySetResult(true);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://first:5001"],
            TspSolverTests.Request() with { PopulationSize = 16, Generations = 2 },
            cancellation.Token, pollInterval: TimeSpan.FromSeconds(2), jobsPerService: 2);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            manager.UpdateServices(["http://first:5001", "http://second:5002"]);
            await secondStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
        }
        finally
        {
            releaseFirst.TrySetResult(true);
        }
        await computation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(manager.LastWarnings);
    }

    [Fact]
    public async Task FailedJobWaitsForAnotherServiceAlreadyExecutingAJob()
    {
        var healthyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHealthy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int brokenAttempts = 0;
        int healthyAttempts = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();

            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            if (message.RequestUri!.Host == "broken")
            {
                Interlocked.Increment(ref brokenAttempts);
                await healthyStarted.Task.WaitAsync(token);
                throw new HttpRequestException("сервис отключился");
            }

            Interlocked.Increment(ref healthyAttempts);
            healthyStarted.TrySetResult(true);
            if (Volatile.Read(ref healthyAttempts) == 1)
                await releaseHealthy.Task.WaitAsync(token);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://broken:5001", "http://healthy:5002"],
            TspSolverTests.Request() with { PopulationSize = 8, Generations = 2 },
            cancellation.Token, pollInterval: TimeSpan.FromMilliseconds(15));
        try
        {
            await healthyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(120);
            Assert.Equal(1, Volatile.Read(ref brokenAttempts));
        }
        finally
        {
            releaseHealthy.TrySetResult(true);
        }

        await computation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Volatile.Read(ref brokenAttempts));
        Assert.Equal(2, Volatile.Read(ref healthyAttempts));
        Assert.DoesNotContain(manager.LastWarnings, warning => warning.Contains("Не завершено заданий"));
    }

    [Fact]
    public async Task CompletedRequestDoesNotWaitForTheNextHeartbeat()
    {
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();

            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            await Task.Delay(60, token);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        SolveResponse result = await manager.SolveDynamicallyAsync(
            ["http://service:5001"],
            TspSolverTests.Request() with { PopulationSize = 4, Generations = 2 },
            CancellationToken.None, pollInterval: TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromMilliseconds(1400));

        Assert.Equal(80, result.Length);
    }

    [Fact]
    public async Task CancellationStopsTheMonitoredRequest()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentQueue<ServiceSnapshot>();
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();

            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://service:5001"],
            TspSolverTests.Request() with { PopulationSize = 4, Generations = 2 },
            cancellation.Token, new InlineProgress(snapshots.Enqueue),
            pollInterval: TimeSpan.FromSeconds(2));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await computation.WaitAsync(TimeSpan.FromSeconds(3));
        });
        Assert.Single(snapshots.Where(snapshot => snapshot.State == ServiceRunState.Finished));
        Assert.Equal(ServiceRunState.Finished, snapshots.Last().State);
    }

    [Fact]
    public async Task RemovedServiceStaysDisconnectedDuringMonitoringAndCompletion()
    {
        var monitoringStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeMonitoring = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeJob = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitoringReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentBag<(bool Removed, ServiceSnapshot Snapshot)>();
        int jobStarted = 0;
        int removed = 0;

        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
            {
                if (Volatile.Read(ref jobStarted) != 0)
                {
                    monitoringStarted.TrySetResult(true);
                    await resumeMonitoring.Task.WaitAsync(token);
                }
                return Status();
            }

            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            Volatile.Write(ref jobStarted, 1);
            await resumeJob.Task.WaitAsync(token);
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        var progress = new InlineProgress(snapshot =>
        {
            snapshots.Add((Volatile.Read(ref removed) != 0, snapshot));
            if (snapshot.JobId is not null && snapshot.State == ServiceRunState.Disconnected)
                monitoringReported.TrySetResult(true);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://service:5001"],
            TspSolverTests.Request() with { PopulationSize = 4, Generations = 2 },
            cancellation.Token, progress, pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            await monitoringStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            manager.UpdateServices([]);
            Volatile.Write(ref removed, 1);
            resumeMonitoring.TrySetResult(true);
            await monitoringReported.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            resumeMonitoring.TrySetResult(true);
            resumeJob.TrySetResult(true);
        }

        SolveResponse result = await computation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(80, result.Length);
        Assert.Contains(snapshots, item => item.Removed && item.Snapshot.JobId is not null);
        Assert.DoesNotContain(snapshots, item => item.Removed &&
            item.Snapshot.State != ServiceRunState.Disconnected);
        Assert.Single(snapshots.Where(item => item.Snapshot.Detail == "Обработка завершена" &&
            item.Snapshot.State == ServiceRunState.Disconnected));
    }

    [Fact]
    public async Task RemovedServiceCannotAcquireWorkAfterItsStatusRequestReturns()
    {
        var statusStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeStatus = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentBag<ServiceSnapshot>();
        int oldJobs = 0;

        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
            {
                if (message.RequestUri!.Host == "old")
                {
                    statusStarted.TrySetResult(true);
                    await resumeStatus.Task.WaitAsync(token);
                }
                return Status();
            }

            if (message.RequestUri!.Host == "old")
                Interlocked.Increment(ref oldJobs);
            SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                cancellationToken: token))!;
            return Reply(request);
        });

        var manager = new GaManagerClient(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://old:5001"],
            TspSolverTests.Request() with { PopulationSize = 4, Generations = 2 },
            cancellation.Token, new InlineProgress(reports.Add),
            pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            await statusStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            manager.UpdateServices(["http://replacement:5002"]);
        }
        finally
        {
            resumeStatus.TrySetResult(true);
        }

        SolveResponse result = await computation.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(80, result.Length);
        Assert.Equal(0, oldJobs);
        Assert.Contains(reports, item => item.ServiceUrl == "http://old:5001" &&
            item.State == ServiceRunState.Disconnected);
        Assert.DoesNotContain(reports, item => item.ServiceUrl == "http://old:5001" &&
            item.State == ServiceRunState.Ready);
    }

    [Fact]
    public async Task RepeatedServiceChangesDoNotStartConcurrentJobs()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        using HttpClient client = FakeClient(async (message, token) =>
        {
            if (message.Method == HttpMethod.Get)
                return Status();
            int active = Interlocked.Increment(ref running);
            if (active != 1)
                throw new InvalidOperationException("Одновременные задания на одном сервисе.");
            try
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(token);
                SolveRequest request = (await message.Content!.ReadFromJsonAsync<SolveRequest>(
                    cancellationToken: token))!;
                return Reply(request);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        });
        var manager = new GaManagerClient(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<SolveResponse> computation = manager.SolveDynamicallyAsync(
            ["http://first:5001"], TspSolverTests.Request() with { PopulationSize = 8, Generations = 2 },
            cancellation.Token, pollInterval: TimeSpan.FromMilliseconds(10), jobsPerService: 2);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int i = 0; i < 5; i++)
            {
                manager.UpdateServices([]);
                manager.UpdateServices(["http://first:5001"]);
            }
        }
        finally
        {
            release.TrySetResult(true);
        }
        Assert.Equal(80, (await computation.WaitAsync(TimeSpan.FromSeconds(3))).Length);
        Assert.Equal(0, Volatile.Read(ref running));
        Assert.Empty(manager.LastWarnings);
    }

    [Fact]
    public void ServicesCannotBeUpdatedWithoutAnActiveComputation()
    {
        using HttpClient client = FakeClient((_, _) => Task.FromResult(Status()));
        var manager = new GaManagerClient(client);
        Assert.Throws<InvalidOperationException>(() => manager.UpdateServices(["http://service:5001"]));
    }

    private static HttpResponseMessage Status() =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServiceStatusResponse("worker-instance", [])) };

    private static HttpResponseMessage Reply(SolveRequest request) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new SolveResponse(
                [0, 1, 3, 2], 80, request.Generations,
                [new GenerationPoint(0, 100), new GenerationPoint(request.Generations, 80)]))
        };

    private static HttpClient FakeClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) =>
        new(new FakeHandler(callback)) { Timeout = Timeout.InfiniteTimeSpan };


    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback = callback;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            callback(request, token);
    }

    private sealed class InlineProgress(Action<ServiceSnapshot> onProgress) : IProgress<ServiceSnapshot>
    {
        private readonly Action<ServiceSnapshot> onProgress = onProgress;

        public void Report(ServiceSnapshot value) => onProgress(value);
    }
}
