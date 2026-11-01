using GaLab.Shared;

namespace GaLab.Manager;

internal sealed record ServiceCallResult(int Index, SolveResponse? Response, string? Warning)
{
    public static ServiceCallResult Succeeded(int index, SolveResponse response) => new(index, response, null);

    public static ServiceCallResult Failed(int index, Uri endpoint, string message) =>
        new(index, null, "Сервис " + endpoint.GetLeftPart(UriPartial.Authority) + ": " + message);
}

internal sealed record MonitoredServiceCallResult(ServiceCallResult? Result, bool ConnectionLost);
