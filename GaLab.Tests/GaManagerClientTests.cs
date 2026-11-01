using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using GaLab.Manager;
using GaLab.Shared;

namespace GaLab.Tests;

public class GaManagerClientTests
{
    [Fact]
    public async Task TwoWorkers_ReturnsShortestValidTour()
    {
        using var client = FakeClient((message, _) => Reply(message.RequestUri!.Host == "first" ? 95 : 80));
        var manager = new GaManagerClient(client);
        SolveResponse result = await manager.SolveAsync(
            ["http://first:5001", "http://second:5002"], TspSolverTests.Request(), CancellationToken.None);
        Assert.Equal(80, result.Length);
        Assert.Equal([0, 1, 3, 2], result.Route);
        Assert.Empty(manager.LastWarnings);
    }

    [Fact]
    public async Task DistributePopulationWithoutSingleIndividualWorker()
    {
        var observed = new ConcurrentBag<(string Host, SolveRequest Request)>();
        using var client = FakeClient(async (message, token) =>
        {
            SolveRequest? req = await message.Content!.ReadFromJsonAsync<SolveRequest>(cancellationToken: token);
            Assert.NotNull(req);
            observed.Add((message.RequestUri!.Host, req));
            return await Reply(80);
        });
        var manager = new GaManagerClient(client);
        await manager.SolveAsync(["http://first:5001", "http://second:5002", "http://third:5003"],
            TspSolverTests.Request() with { PopulationSize = 5, Seed = 100 }, CancellationToken.None);
        Assert.Equal(2, observed.Count);
        Assert.Equal(5, observed.Sum(o => o.Request.PopulationSize));
        Assert.Contains(observed, x => x.Host == "first" && x.Request.PopulationSize == 3 && x.Request.Seed == 100);
        Assert.Contains(observed, x => x.Host == "second" && x.Request.PopulationSize == 2 && x.Request.Seed == 101);
        Assert.Contains(manager.LastWarnings, w => w.Contains("Использовано 2 сервис"));
        Assert.All(observed, x => Assert.True(
            x.Request.Distances.SelectMany(row => row)
                .SequenceEqual(TspSolverTests.Matrix().SelectMany(row => row))));
    }

    [Fact]
    public async Task OneWorkerFails_ReturnsOtherWorkerResult()
    {
        using var client = FakeClient((message, _) =>
            message.RequestUri!.Host == "broken" ? throw new HttpRequestException("Сервис выключен") : Reply(80));
        var manager = new GaManagerClient(client);
        SolveResponse result = await manager.SolveAsync(["http://broken:5001", "http://second:5002"],
            TspSolverTests.Request(), CancellationToken.None);
        Assert.Equal(80, result.Length);
        Assert.Single(manager.LastWarnings);
    }

    [Fact]
    public async Task ServerReturns503_ReturnsOtherWorkerResult()
    {
        using var client = FakeClient((message, _) =>
            message.RequestUri!.Host == "broken"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : Reply(80));
        var manager = new GaManagerClient(client);
        SolveResponse result = await manager.SolveAsync(["http://broken:5001", "http://second:5002"],
            TspSolverTests.Request(), CancellationToken.None);
        Assert.Equal(80, result.Length);
        Assert.Single(manager.LastWarnings);
    }

    [Fact]
    public async Task WrongTourLength_IsRejected()
    {
        using var client = FakeClient((message, _) =>
        {
            var invalid = new SolveResponse([0, 1, 3, 2], 10, 2,
                [new GenerationPoint(2, 10)]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(invalid) });
        });
        var manager = new GaManagerClient(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SolveAsync(
            ["http://first:5001"], TspSolverTests.Request(), CancellationToken.None));
        Assert.Single(manager.LastWarnings);
    }

    [Fact]
    public async Task AllWorkersFail_ThrowsClearError()
    {
        using var client = FakeClient((message, _) => throw new HttpRequestException("Нет соединения"));
        var manager = new GaManagerClient(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SolveAsync(
            ["http://first:5001", "http://second:5002"], TspSolverTests.Request(), CancellationToken.None));
        Assert.Equal(2, manager.LastWarnings.Count);
    }

    [Fact]
    public async Task WorkerTimeout_DoesNotDiscardOtherResult()
    {
        using var client = FakeClient(async (message, token) =>
        {
            if (message.RequestUri!.Host == "slow")
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return await Reply(80);
        });
        var manager = new GaManagerClient(client, TimeSpan.FromMilliseconds(100));
        SolveResponse response = await manager.SolveAsync(
            ["http://slow:5001", "http://fast:5002"], TspSolverTests.Request(), CancellationToken.None);
        Assert.Equal(80, response.Length);
        Assert.Single(manager.LastWarnings);
    }

    [Fact]
    public async Task OverflowedRouteLength_IsRejected()
    {
        using HttpClient client = FakeClient((_, _) => Reply(80));
        var manager = new GaManagerClient(client);
        double[][] distances = TspSolverTests.Matrix();
        for (int row = 0; row < distances.Length; row++)
            for (int column = 0; column < distances[row].Length; column++)
                distances[row][column] = row == column ? 0 : double.MaxValue;

        SolveRequest request = TspSolverTests.Request() with { Distances = distances };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SolveAsync(
            ["http://first:5001"], request, CancellationToken.None));
        Assert.Contains(manager.LastWarnings,
            warning => warning.Contains("переполнение при вычислении длины маршрута"));
    }

    private static Task<HttpResponseMessage> Reply(int length)
    {
        int[] route = length == 80 ? [0, 1, 3, 2] : [0, 1, 2, 3];
        var response = new SolveResponse(route, length, 2, [new GenerationPoint(0, 100), new GenerationPoint(2, length)]);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
    }

    private static HttpClient FakeClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        => new(new FakeHttpMessageHandler(callback));

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback = callback;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => callback(request, cancellationToken);
    }
}
