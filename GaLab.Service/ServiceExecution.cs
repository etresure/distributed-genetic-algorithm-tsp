using GaLab.Shared;

namespace GaLab.Service;

public sealed class ServiceExecution(TspServiceSolver solver)
{
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private JobProgress? activeJob;

    public ServiceStatusResponse GetStatus()
    {
        JobProgress? job = Volatile.Read(ref activeJob);
        return new ServiceStatusResponse(instanceId, job is null ? [] : [job]);
    }

    public bool TrySolve(SolveRequest request, CancellationToken cancellationToken, out SolveResponse? response)
    {
        ArgumentNullException.ThrowIfNull(request);
        string jobId = request.JobId ?? Guid.NewGuid().ToString("N");
        JobProgress initial = new(jobId, 0, request.Generations, null);
        if (Interlocked.CompareExchange(ref activeJob, initial, null) is not null)
        {
            response = null;
            return false;
        }

        try
        {
            response = solver.Solve(request, cancellationToken, generation =>
                Volatile.Write(ref activeJob, new JobProgress(
                    jobId, generation.Generation, request.Generations, generation.Length)));
            return true;
        }
        finally
        {
            Volatile.Write(ref activeJob, null);
        }
    }
}
