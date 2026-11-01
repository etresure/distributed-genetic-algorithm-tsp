using GaLab.Service;
using GaLab.Shared;
using TspLibrary;

namespace GaLab.Tests;

public class TspSolverTests
{
    [Fact]
    public void ProblemData_ProducesSymmetricEuclideanDistances()
    {
        double[,] matrix = ProblemData.CreateDistances(14, 123);
        for (int i = 0; i < 14; i++)
        {
            Assert.Equal(0, matrix[i, i]);
            for (int j = i + 1; j < 14; j++)
            {
                Assert.True(matrix[i, j] > 0);
                Assert.Equal(matrix[i, j], matrix[j, i]);
                for (int k = 0; k < 14; k++)
                    Assert.True(matrix[i, j] <= matrix[i, k] + matrix[k, j] + 1e-8);
            }
        }
    }

    [Fact]
    public void Solver_ProducesClosedTourAndDeterministicResults()
    {
        SolveRequest req = Request();
        var solver = new TspServiceSolver();
        SolveResponse a = solver.Solve(req);
        SolveResponse b = solver.Solve(req);
        Assert.True(a.Route.SequenceEqual(b.Route));
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(4, a.Route.Distinct().Count());
        Assert.Equal(4, a.Route.Length);
        Assert.InRange(a.Length, 80.0, 95.0);
        Assert.Equal(req.Generations, a.Generation);
        Assert.NotEmpty(a.History);
        Assert.Equal(a.Length, a.History[^1].Length);
        Assert.Equal(a.Generation, a.History[^1].Generation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Solver_RejectsPopulationSmallerThanTwo(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TspServiceSolver().Solve(Request() with { PopulationSize = size }));
    }

    [Fact]
    public void Solver_RejectsAsymmetricMatrix()
    {
        var broken = Request() with
        {
            Distances =
        [
            [0,10,15,20],
            [11,0,35,25],
            [15,35,0,30],
            [20,25,30,0]
        ]
        };
        Assert.Throws<ArgumentException>(() => new TspServiceSolver().Solve(broken));
    }

    [Fact]
    public void Solver_CancellationDoesNotUseIncompleteGeneration()
    {
        using var source = new CancellationTokenSource();
        GenerationResult last = TspSolver.Solve(Matrix2D(), 30, 200, 0.2,
            result => { if (result.Generation == 3) source.Cancel(); }, source.Token,
            maxDegreeOfParallelism: 1, seed: 42);
        Assert.Equal(3, last.Generation);
    }

    public static SolveRequest Request() => new(Matrix(), 30, 25, 0.2, 42);

    public static double[][] Matrix() =>
    [
        [0,10,15,20],
        [10,0,35,25],
        [15,35,0,30],
        [20,25,30,0]
    ];

    public static double[,] Matrix2D() => new double[,] {
        {0,10,15,20}, {10,0,35,25}, {15,35,0,30}, {20,25,30,0}
    };
}
