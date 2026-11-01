using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using GaLab.Shared;

namespace GaLab.Manager;

internal sealed class ServiceCommunication(HttpClient httpClient, TimeSpan requestTimeout)
{
    public async Task<ServiceCallResult> SolveAsync(
        Uri serviceUrl, SolveRequest request, int index, CancellationToken cancellationToken)
    {
        Uri endpoint = new(serviceUrl.ToString().TrimEnd('/') + "/api/tsp/solve");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using HttpResponseMessage httpResponse = await httpClient.PostAsJsonAsync(endpoint, request, timeout.Token);
            if (!httpResponse.IsSuccessStatusCode)
            {
                string body = await httpResponse.Content.ReadAsStringAsync(timeout.Token);
                return ServiceCallResult.Failed(index, endpoint,
                    $"HTTP {(int)httpResponse.StatusCode}: " + (string.IsNullOrWhiteSpace(body) ? httpResponse.ReasonPhrase : body.Trim()));
            }
            SolveResponse? response = await httpResponse.Content.ReadFromJsonAsync<SolveResponse>(
                cancellationToken: timeout.Token);
            if (response is null)
                return ServiceCallResult.Failed(index, endpoint, "пустой ответ");
            string? problem = TspResponseValidator.Validate(response, request);
            return problem is null ? ServiceCallResult.Succeeded(index, response)
                : ServiceCallResult.Failed(index, endpoint, problem);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ServiceCallResult.Failed(index, endpoint, $"тайм-аут {requestTimeout.TotalSeconds:G} секунд");
        }
        catch (HttpRequestException exception)
        {
            return ServiceCallResult.Failed(index, endpoint, exception.Message);
        }
        catch (JsonException exception)
        {
            return ServiceCallResult.Failed(index, endpoint, "ошибка ответа: " + exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return ServiceCallResult.Failed(index, endpoint, "ошибка ответа: " + exception.Message);
        }
    }

    public async Task<MonitoredServiceCallResult> SolveMonitoredAsync(
        Uri serviceUrl,
        string instanceId,
        SolveRequest job,
        int index,
        TimeSpan pollInterval,
        CancellationToken cancellationToken,
        Action<int, string?> reportProgress,
        Action<string> reportFailure)
    {
        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ServiceCallResult> request = SolveAsync(serviceUrl, job, index, requestCancellation.Token);
        bool connectionLost = false;
        reportProgress(0, null);

        try
        {
            while (!request.IsCompleted)
            {
                if (await Task.WhenAny(request, Task.Delay(pollInterval, cancellationToken)) == request)
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                if (request.IsCompleted)
                    break;

                ServiceStatusResponse? status = await GetStatusAsync(serviceUrl, pollInterval, cancellationToken);
                if (status is null)
                {
                    reportFailure("Сервис не отвечает");
                    continue;
                }

                if (!string.Equals(status.InstanceId, instanceId, StringComparison.Ordinal))
                {
                    connectionLost = true;
                    reportFailure("Сервис перезапущен");
                    requestCancellation.Cancel();
                    break;
                }

                JobProgress? activeJob = status.ActiveJobs.FirstOrDefault(
                    candidate => string.Equals(candidate.JobId, job.JobId, StringComparison.Ordinal));
                string? detail = activeJob?.BestLength is double length
                    ? $"Лучший маршрут: {length:G8}"
                    : "Ожидание прогресса";
                reportProgress(activeJob?.Generation ?? 0, detail);
            }

            ServiceCallResult result = await request;
            return new MonitoredServiceCallResult(connectionLost ? null : result, connectionLost);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectionLost)
        {
            return new MonitoredServiceCallResult(null, true);
        }
        finally
        {
            requestCancellation.Cancel();
            try
            {
                await request;
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
            }
        }
    }

    public async Task<ServiceStatusResponse?> GetStatusAsync(
        Uri serviceUrl, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, pollInterval.TotalMilliseconds * 2)));

        try
        {
            Uri endpoint = new(serviceUrl.ToString().TrimEnd('/') + "/api/tsp/status");
            using HttpResponseMessage response = await httpClient.GetAsync(endpoint, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            ServiceStatusResponse? status = await response.Content.ReadFromJsonAsync<ServiceStatusResponse>(
                cancellationToken: timeout.Token);
            return status is { InstanceId.Length: > 0, ActiveJobs: not null } ? status : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

}
