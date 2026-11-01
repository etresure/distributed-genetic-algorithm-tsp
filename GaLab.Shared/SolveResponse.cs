namespace GaLab.Shared;

public sealed record GenerationPoint(int Generation, double Length);

/// <summary>One worker result. Route uses zero-based city indices.</summary>
public sealed record SolveResponse(
    int[] Route,
    double Length,
    int Generation,
    GenerationPoint[] History);
