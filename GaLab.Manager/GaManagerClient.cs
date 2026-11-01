using System.Net.Http;
using GaLab.Shared;

namespace GaLab.Manager;

public sealed partial class GaManagerClient
{
    private readonly ServiceCommunication communication;

    public GaManagerClient(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        TimeSpan timeout = requestTimeout ?? TimeSpan.FromMinutes(5);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        communication = new ServiceCommunication(httpClient, timeout);
    }

    public IReadOnlyList<string> LastWarnings { get; private set; } = [];

    public async Task<SolveResponse> SolveAsync(
        IReadOnlyList<string> serviceUrls,
        SolveRequest baseRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceUrls);
        ValidateRequest(baseRequest);

        var warnings = new List<string>();
        var urls = ParseServiceUrls(serviceUrls, warnings);
        LastWarnings = [.. warnings];
        if (urls.Count == 0)
            throw new InvalidOperationException("Не указано ни одного корректного адреса вычислительного сервиса.");

        // TspSolver requires >=2 individuals per independent run. Do not send size=1.
        int usedServices = Math.Min(urls.Count, baseRequest.PopulationSize / 2);
        if (usedServices < urls.Count)
            warnings.Add($"Использовано {usedServices} сервис(ов) из {urls.Count}: каждому нужно минимум 2 особи.");
        int share = baseRequest.PopulationSize / usedServices;
        int remainder = baseRequest.PopulationSize % usedServices;

        var tasks = new List<Task<ServiceCallResult>>(usedServices);
        for (int i = 0; i < usedServices; i++)
        {
            var taskRequest = baseRequest with
            {
                PopulationSize = share + (i < remainder ? 1 : 0),
                Seed = baseRequest.Seed.HasValue ? unchecked(baseRequest.Seed.Value + i) : null
            };
            tasks.Add(communication.SolveAsync(urls[i], taskRequest, i, cancellationToken));
        }

        ServiceCallResult[] results = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (ServiceCallResult result in results.OrderBy(result => result.Index))
            if (result.Warning is not null)
                warnings.Add(result.Warning);
        LastWarnings = [.. warnings];

        SolveResponse? best = results
            .Where(result => result.Response is not null)
            .Select(result => result.Response!)
            .OrderBy(result => result.Length)
            .FirstOrDefault();

        return best ?? throw new InvalidOperationException(
            "Все сервисы недоступны или вернули ошибку. Проверьте их адреса и запустите хотя бы один сервис.");
    }

    private static void ValidateRequest(SolveRequest baseRequest)
    {
        ArgumentNullException.ThrowIfNull(baseRequest);
        if (baseRequest.PopulationSize < 2)
            throw new ArgumentException("В популяции должно быть не менее двух особей.", nameof(baseRequest));
        if (baseRequest.Distances is null || baseRequest.Distances.Length < 3)
            throw new ArgumentException("Нужна матрица минимум для трёх городов.", nameof(baseRequest));
    }

    private static List<Uri> ParseServiceUrls(IReadOnlyList<string> values, List<string> warnings)
    {
        var valid = new List<Uri>();
        foreach (string? item in values)
        {
            string text = item?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                warnings.Add("Пропущен некорректный адрес сервиса: " + (text.Length == 0 ? "<пусто>" : text));
                continue;
            }
            valid.Add(uri);
        }
        return valid;
    }

}
