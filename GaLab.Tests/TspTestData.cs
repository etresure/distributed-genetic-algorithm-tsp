using GaLab.Shared;

namespace GaLab.Tests;

internal static class TspTestData
{
    // Square in Manhattan metric: the perimeter route is length 40;
    // other correct permutations can have length 60.
    public static double[][] Distances() =>
    [
        [0.0, 10.0, 20.0, 10.0],
        [10.0, 0.0, 10.0, 20.0],
        [20.0, 10.0, 0.0, 10.0],
        [10.0, 20.0, 10.0, 0.0]
    ];

    public static SolveRequest Request(int populationSize = 20, int generations = 35,
        int? seed = 42) => new(Distances(), populationSize, generations, 0.2, seed);

    public static SolveResponse Response(int[] route, int generation = 35)
    {
        double[][] d = Distances();
        double length = 0;
        for (int i = 0; i < route.Length; i++)
            length += d[route[i]][route[(i + 1) % route.Length]];
        return new SolveResponse(route, length, generation,
            [new GenerationPoint(generation, length)]);
    }

    public static void AssertValidRoute(SolveResponse result, double[][] distances)
    {
        Assert.Equal(distances.Length, result.Route.Length);
        Assert.Equal(Enumerable.Range(0, distances.Length), result.Route.OrderBy(x => x));
        double length = 0;
        for (int i = 0; i < result.Route.Length; i++)
            length += distances[result.Route[i]][result.Route[(i + 1) % result.Route.Length]];
        Assert.Equal(length, result.Length, 7);
        Assert.Equal(result.Generation, result.History[^1].Generation);
        Assert.Equal(result.Length, result.History[^1].Length, 7);
    }
}
