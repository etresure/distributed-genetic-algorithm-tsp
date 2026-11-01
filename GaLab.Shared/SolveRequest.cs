namespace GaLab.Shared;

/// <summary>One TSP run. Each worker receives the SAME distance matrix, but its own seed/population.</summary>
public sealed record SolveRequest(
    double[][] Distances,
    int PopulationSize,
    int Generations,
    double MutationRate,
    int? Seed)
{
    public string? JobId { get; init; }
}
