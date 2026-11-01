using GaLab.Shared;
using TspLibrary;

namespace GaLab.Service;

/// <summary>HTTP boundary around the TSP solver FROM LAB 1 (OX, swap-mutation, elitism).</summary>
public sealed class TspServiceSolver
{
    public SolveResponse Solve(
        SolveRequest request, CancellationToken cancellationToken = default, Action<GenerationPoint>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        double[,] distances = ValidateAndConvertDistances(request.Distances);
        // TspSolver performs the rest of the checks (population >= 2, generations >= 1, mutation 0..1).
        Queue<GenerationPoint> lastGenerations = new();
        GenerationResult result = TspSolver.Solve(
            distances,
            request.PopulationSize,
            request.Generations,
            request.MutationRate,
            generation =>
            {
                GenerationPoint point = new(generation.Generation, generation.Length);
                progress?.Invoke(point);
                lastGenerations.Enqueue(point);
                if (lastGenerations.Count > 60)
                    lastGenerations.Dequeue();
            },
            cancellationToken,
            maxDegreeOfParallelism: -1,
            seed: request.Seed);

        return new SolveResponse(result.Route, result.Length, result.Generation, [.. lastGenerations]);
    }

    private static double[,] ValidateAndConvertDistances(double[][]? data)
    {
        if (data is null || data.Length is < 3 or > 200)
            throw new ArgumentException("Матрица расстояний должна содержать от 3 до 200 городов.");

        int n = data.Length;
        var distances = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            if (data[i] is null || data[i].Length != n)
                throw new ArgumentException("Матрица расстояний должна быть квадратной.");
            for (int j = 0; j < n; j++)
            {
                double value = data[i][j];
                if (!double.IsFinite(value) || (i == j ? value != 0 : value <= 0))
                    throw new ArgumentException("Матрица содержит недопустимое расстояние.");
                distances[i, j] = value;
            }
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (distances[i, j] != distances[j, i])
                    throw new ArgumentException("Матрица расстояний должна быть симметричной.");
        return distances;
    }
}
