namespace GaLab.Manager;

public enum ServiceRunState
{
    Ready,
    Busy,
    Running,
    Reconnecting,
    Finished,
    Disconnected
}

public sealed record ServiceSnapshot(
    string ServiceUrl,
    ServiceRunState State,
    string? JobId,
    int Generation,
    int TotalGenerations,
    int CompletedJobs,
    string? Detail = null);
