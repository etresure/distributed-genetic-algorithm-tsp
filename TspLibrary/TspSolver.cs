using System;
using System.Threading;
using System.Threading.Tasks;

namespace TspLibrary
{
    public class GenerationResult(int generation, int[] route, double length)
    {
        public int Generation { get; private set; } = generation;
        public int[] Route { get; private set; } = (int[])route.Clone();
        public double Length { get; private set; } = length;
    }

    public class TspSolver
    {
        internal class Individual(int[] route, double length)
        {
            public int[] Route { get; private set; } = route;
            public double Length { get; private set; } = length;
        }

        public static GenerationResult Solve(
            double[,] distances,
            int populationSize,
            int generationCount,
            double mutationProbability,
            Action<GenerationResult> report,
            CancellationToken cancellationToken,
            int maxDegreeOfParallelism = -1,
            int? seed = null)
        {
            ValidateParameters(distances, populationSize, generationCount, mutationProbability);
            if (maxDegreeOfParallelism != -1 && maxDegreeOfParallelism < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
            }

            int cityCount = distances.GetLength(0);
            Random random = seed.HasValue ? new Random(seed.Value) : new Random();
            Individual[] population = new Individual[populationSize];

            // Начальная популяция считается целиком, чтобы при остановке был готовый ответ.
            for (int i = 0; i < populationSize; i++)
            {
                int[] route = CreateRandomRoute(cityCount, random);
                population[i] = new Individual(route, CalculateLength(route, distances));
            }

            Individual best = FindBest(population);
            GenerationResult result = new(0, best.Route, best.Length);
            report?.Invoke(result);

            ParallelOptions options = new()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
            };

            for (int generation = 0; generation < generationCount; generation++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Individual[] nextPopulation = new Individual[populationSize];
                // Элитизм сохраняет лучший маршрут без изменений.
                nextPopulation[0] = best;

                try
                {
                    // Свой seed для каждого потомка: результат не зависит от планирования потоков.
                    int generationSeed = random.Next();
                    void createChild(int index)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Random childRandom = new(unchecked(generationSeed + index * 1000003));
                        nextPopulation[index] = CreateOffspring(
                            population, distances, mutationProbability, childRandom);
                    }
                    if (maxDegreeOfParallelism == 1)
                    {
                        for (int index = 1; index < populationSize; index++)
                            createChild(index);
                    }
                    else
                    {
                        Parallel.For(1, populationSize, options, createChild);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Частично рассчитанное поколение не заменяет готовую популяцию.
                    break;
                }

                population = nextPopulation;
                best = FindBest(population);
                result = new GenerationResult(generation + 1, best.Route, best.Length);
                report?.Invoke(result);
            }

            return result;
        }

        // Общий шаг эволюции для последовательного и параллельного расчета.
        // Родители доступны только для чтения; изменяется исключительно новый потомок.
        internal static Individual CreateOffspring(
            Individual[] population, double[,] distances, double mutationProbability, Random random)
        {
            Individual first = SelectParent(population, random);
            Individual second = SelectParent(population, random);
            int[] child = Crossover(first.Route, second.Route, random);
            if (random.NextDouble() < mutationProbability)
            {
                Mutate(child, random);
            }
            return new Individual(child, CalculateLength(child, distances));
        }

        private static void ValidateParameters(
            double[,] distances, int populationSize,
            int generationCount, double mutationProbability)
        {
            ArgumentNullException.ThrowIfNull(distances);

            int cityCount = distances.GetLength(0);
            if (cityCount < 3 || distances.GetLength(1) != cityCount)
            {
                throw new ArgumentException("Матрица должна быть квадратной, размером не менее 3.");
            }

            if (populationSize < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(populationSize),
                    "В популяции должно быть не менее двух особей.");
            }

            if (generationCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(generationCount),
                    "Количество поколений должно быть положительным.");
            }

            if (double.IsNaN(mutationProbability) ||
                mutationProbability < 0 || mutationProbability > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(mutationProbability),
                    "Вероятность мутации должна быть от 0 до 1.");
            }

            for (int i = 0; i < cityCount; i++)
            {
                if (distances[i, i] != 0)
                {
                    throw new ArgumentException("На диагонали матрицы должны быть нули.");
                }

                for (int j = i + 1; j < cityCount; j++)
                {
                    double distance = distances[i, j];
                    if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0)
                    {
                        throw new ArgumentException("Расстояния должны быть положительными конечными числами.");
                    }

                    if (distance != distances[j, i])
                    {
                        throw new ArgumentException("Матрица расстояний должна быть симметричной.");
                    }
                }
            }
        }

        private static int[] CreateRandomRoute(int cityCount, Random random)
        {
            int[] route = new int[cityCount];
            for (int i = 0; i < cityCount; i++)
            {
                route[i] = i;
            }

            for (int i = cityCount - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (route[j], route[i]) = (route[i], route[j]);
            }

            return route;
        }

        private static double CalculateLength(int[] route, double[,] distances)
        {
            double length = 0;
            for (int i = 0; i < route.Length; i++)
            {
                int next = (i + 1) % route.Length;
                length += distances[route[i], route[next]];
            }

            if (double.IsInfinity(length))
            {
                throw new ArgumentException("Длина маршрута превышает допустимый диапазон double.");
            }

            return length;
        }

        private static Individual FindBest(Individual[] population)
        {
            Individual best = population[0];
            for (int i = 1; i < population.Length; i++)
            {
                if (population[i].Length < best.Length)
                {
                    best = population[i];
                }
            }

            return best;
        }

        private static Individual SelectParent(Individual[] population, Random random)
        {
            Individual best = population[random.Next(population.Length)];
            for (int i = 1; i < 3; i++)
            {
                Individual candidate = population[random.Next(population.Length)];
                if (candidate.Length < best.Length)
                {
                    best = candidate;
                }
            }

            return best;
        }

        internal static int[] Crossover(int[] first, int[] second, Random random)
        {
            int cityCount = first.Length;
            int a = random.Next(cityCount);
            int b = random.Next(cityCount);
            int start = Math.Min(a, b);
            int end = Math.Max(a, b);
            int[] child = new int[cityCount];
            bool[] used = new bool[cityCount];

            for (int i = start; i <= end; i++)
            {
                child[i] = first[i];
                used[first[i]] = true;
            }

            // OX сохраняет фрагмент первого родителя и порядок остальных городов второго.
            int position = (end + 1) % cityCount;
            for (int i = 0; i < cityCount; i++)
            {
                int city = second[(end + 1 + i) % cityCount];
                if (!used[city])
                {
                    child[position] = city;
                    position = (position + 1) % cityCount;
                }
            }

            return child;
        }

        internal static void Mutate(int[] route, Random random)
        {
            int first = random.Next(route.Length);
            int second = random.Next(route.Length - 1);
            if (second >= first)
            {
                second++;
            }

            (route[second], route[first]) = (route[first], route[second]);
        }
    }
}
