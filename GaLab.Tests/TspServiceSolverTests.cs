using GaLab.Service;
using GaLab.Shared;

namespace GaLab.Tests;

public class TspServiceSolverTests
{
    [Fact]
    public void ProgressCallback_ObservesSameGenerationsAsResponse()
    {
        SolveRequest request = TspTestData.Request() with { Generations = 2 };
        List<GenerationPoint> reported = [];
        SolveResponse response = new TspServiceSolver().Solve(request, progress: reported.Add);

        Assert.Equal(response.History, reported);
    }

    [Fact]
    public void SameSeed_ProducesSameRouteAndHistory()
    {
        var solver = new TspServiceSolver();
        SolveRequest request = TspTestData.Request();
        SolveResponse a = solver.Solve(request);
        SolveResponse b = solver.Solve(request);
        Assert.Equal(a.Route, b.Route);
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a.History, b.History);
        TspTestData.AssertValidRoute(a, request.Distances);
    }

    [Fact]
    public void Result_VisitsEachCityOnceAndReturnsToStart()
    {
        SolveRequest request = TspTestData.Request(12, 25);
        SolveResponse response = new TspServiceSolver().Solve(request);
        TspTestData.AssertValidRoute(response, request.Distances);
        Assert.Equal(request.Generations, response.Generation);
        Assert.Equal(request.Generations + 1, response.History.Length);
        Assert.All(response.History.Zip(response.History.Skip(1)), pair =>
            Assert.True(pair.Second.Length <= pair.First.Length + 1e-8));
    }

    [Fact]
    public void RejectsNonsymmetricDistances()
    {
        SolveRequest request = TspTestData.Request();
        request.Distances[0][1] = 900;
        Assert.Throws<ArgumentException>(() => new TspServiceSolver().Solve(request));
    }

    [Fact]
    public void RejectsPopulationOfOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TspServiceSolver().Solve(TspTestData.Request(populationSize: 1)));
    }

    [Fact]
    public void InitiallyCancelledRun_ReturnsCompleteInitialPopulationResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        SolveResponse response = new TspServiceSolver().Solve(TspTestData.Request(), cancellation.Token);
        Assert.Equal(0, response.Generation);
        Assert.Single(response.History);
        TspTestData.AssertValidRoute(response, TspTestData.Distances());
    }
}
