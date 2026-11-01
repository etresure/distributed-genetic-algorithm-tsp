using GaLab.Shared;

namespace GaLab.Manager;

public sealed partial class GaManagerClient
{
    private DynamicGaScheduler? activeScheduler;

    /// <summary>Изменяет список сервисов для текущего поиска. Уже назначенные задания завершаются на прежних сервисах.</summary>
    public void UpdateServices(IReadOnlyList<string> serviceUrls)
    {
        ArgumentNullException.ThrowIfNull(serviceUrls);
        List<string> warnings = [];
        List<Uri> services = ParseServiceUrls(serviceUrls, warnings);
        if (warnings.Count != 0)
            throw new ArgumentException(warnings[0], nameof(serviceUrls));

        DynamicGaScheduler scheduler = Volatile.Read(ref activeScheduler) ??
            throw new InvalidOperationException("Нет выполняющегося вычисления.");
        scheduler.UpdateServices(services);
    }

    /// <param name="jobsPerService">Значения больше единицы изменяют разбиение популяции.</param>
    public async Task<SolveResponse> SolveDynamicallyAsync(
        IReadOnlyList<string> serviceUrls,
        SolveRequest baseRequest,
        CancellationToken cancellationToken,
        IProgress<ServiceSnapshot>? progress = null,
        TimeSpan? pollInterval = null,
        TimeSpan? reconnectWindow = null,
        int jobsPerService = 1)
    {
        ArgumentNullException.ThrowIfNull(serviceUrls);
        ValidateRequest(baseRequest);
        if (jobsPerService < 1)
            throw new ArgumentOutOfRangeException(nameof(jobsPerService));

        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        TimeSpan idleLimit = reconnectWindow ?? TimeSpan.FromSeconds(30);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        if (idleLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconnectWindow));

        List<string> warnings = [];
        List<Uri> endpoints = ParseServiceUrls(serviceUrls, warnings);
        Uri[] services = [.. endpoints.Distinct()];
        if (services.Length != endpoints.Count)
            warnings.Add("Повторяющиеся адреса сервисов исключены из расписания.");

        if (services.Length == 0)
        {
            LastWarnings = [.. warnings];
            throw new InvalidOperationException("Не указано ни одного корректного адреса вычислительного сервиса.");
        }

        var scheduler = new DynamicGaScheduler(communication, services, baseRequest,
            interval, idleLimit, jobsPerService, progress, warnings);
        if (Interlocked.CompareExchange(ref activeScheduler, scheduler, null) is not null)
            throw new InvalidOperationException("Вычисление уже выполняется.");
        LastWarnings = [.. warnings];
        try
        {
            SolveResponse? response = await scheduler.RunAsync(cancellationToken);
            return response ?? throw new InvalidOperationException(
                "Нет успешных результатов: все сервисы недоступны или задания завершились ошибками.");
        }
        finally
        {
            Interlocked.CompareExchange(ref activeScheduler, null, scheduler);
            LastWarnings = scheduler.Warnings;
        }
    }
}
