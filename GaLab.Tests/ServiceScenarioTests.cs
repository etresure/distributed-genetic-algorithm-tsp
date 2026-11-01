using System.Net;
using System.Net.Http.Json;
using GaLab.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GaLab.Tests;

// Integration/scenario tests: actual HTTP endpoint, JSON and solver in an in-memory web host.
public class ServiceScenarioTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task StatusEndpoint_ReturnsProcessIdentityAndIdleSnapshot()
    {
        ServiceStatusResponse? first = await client.GetFromJsonAsync<ServiceStatusResponse>("/api/tsp/status");
        ServiceStatusResponse? second = await client.GetFromJsonAsync<ServiceStatusResponse>("/api/tsp/status");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(string.IsNullOrWhiteSpace(first.InstanceId));
        Assert.Equal(first.InstanceId, second.InstanceId);
        Assert.Empty(first.ActiveJobs);
    }

    [Fact]
    public async Task TspEndpoint_ValidRequest_ReturnsClosedRouteAndHistory()
    {
        SolveRequest request = TspTestData.Request();
        using HttpResponseMessage response =
            await client.PostAsJsonAsync("/api/tsp/solve", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SolveResponse? result = await response.Content.ReadFromJsonAsync<SolveResponse>();
        Assert.NotNull(result);
        TspTestData.AssertValidRoute(result!, request.Distances);
        Assert.Equal(request.Generations, result!.Generation);
    }

    [Fact]
    public async Task TspEndpoint_InvalidPopulation_ReturnsHttp400()
    {
        SolveRequest request = TspTestData.Request(populationSize: 1);
        using HttpResponseMessage response =
            await client.PostAsJsonAsync("/api/tsp/solve", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("популяции", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TspEndpoint_InvalidMatrix_ReturnsHttp400()
    {
        SolveRequest request = TspTestData.Request() with
        {
            Distances = [[0.0, 1.0], [1.0, 0.0]]
        };
        using HttpResponseMessage response =
            await client.PostAsJsonAsync("/api/tsp/solve", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
