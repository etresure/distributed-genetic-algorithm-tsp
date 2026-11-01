using GaLab.Service;
using GaLab.Shared;

namespace GaLab.Tests;

public sealed class ServiceExecutionTests
{
    [Fact]
    public void Status_IsCleanedUpAfterSuccessfulRunAndFailedRun()
    {
        var service = new ServiceExecution(new TspServiceSolver());
        Assert.Empty(service.GetStatus().ActiveJobs);
        Assert.True(service.TrySolve(TspTestData.Request() with { JobId = "completed" },
            CancellationToken.None, out _));
        Assert.Empty(service.GetStatus().ActiveJobs);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.TrySolve(TspTestData.Request(populationSize: 1) with { JobId = "failed" },
                CancellationToken.None, out _));
        Assert.Empty(service.GetStatus().ActiveJobs);
    }

}
