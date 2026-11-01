using GaLab.Service;
using GaLab.Shared;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TspServiceSolver>();
builder.Services.AddSingleton<ServiceExecution>();
WebApplication app = builder.Build();

app.MapGet("/api/tsp/status", (ServiceExecution service) => service.GetStatus());

app.MapPost("/api/tsp/solve", (SolveRequest request, ServiceExecution service, CancellationToken cancellationToken) =>
{
    try
    {
        if (!service.TrySolve(request, cancellationToken, out SolveResponse? response))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(response);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

string? projectName = typeof(Program).Assembly.GetName().Name;

string defaultAddress = projectName == "GaLab.Service2"
    ? "http://localhost:5002"
    : "http://localhost:5001";
app.Run(builder.Configuration["GALAB_LISTEN_URL"] ?? defaultAddress);

public partial class Program { }
