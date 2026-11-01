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
