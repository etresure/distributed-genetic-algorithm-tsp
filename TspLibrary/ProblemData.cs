using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TspLibrary
{
    public static class ProblemData
    {
        public static double[,] CreateDistances(int cityCount, int seed)
        {
            if (cityCount < 3 || cityCount > 200)
            {
                throw new ArgumentOutOfRangeException(nameof(cityCount));
            }

            Random random = new(seed);
            int[] x = new int[cityCount];
            int[] y = new int[cityCount];
            HashSet<int> occupied = [];

            for (int i = 0; i < cityCount; i++)
            {
                do
                {
                    x[i] = random.Next(1000);
                    y[i] = random.Next(1000);
                }
                while (!occupied.Add(x[i] * 1000 + y[i]));
            }

            // Евклидовы расстояния обеспечивают симметрию и неравенство треугольника.
            double[,] distances = new double[cityCount, cityCount];
            for (int i = 0; i < cityCount; i++)
            {
                for (int j = i + 1; j < cityCount; j++)
                {
                    double dx = x[i] - x[j];
                    double dy = y[i] - y[j];
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    distances[i, j] = distance;
                    distances[j, i] = distance;
                }
            }

            return distances;
        }

        public static string FormatResult(GenerationResult result, double[,] distances)
        {
            int[] route = result.Route;
            int start = Array.IndexOf(route, 0);
            StringBuilder text = new();
            text.AppendLine("Поколение: " + result.Generation);
            text.Append("Маршрут: ");

            // Один и тот же замкнутый маршрут можно начать с любого города.
            for (int i = 0; i <= route.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(" -> ");
                }
                text.Append(route[(start + i) % route.Length] + 1);
            }

            text.AppendLine();
            text.AppendLine("Длины участков:");
            for (int i = 0; i < route.Length; i++)
            {
                int from = route[(start + i) % route.Length];
                int to = route[(start + i + 1) % route.Length];
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} -> {1}: {2:F2}", from + 1, to + 1, distances[from, to]));
            }

            text.AppendFormat(CultureInfo.InvariantCulture,
                "Общая длина: {0:F2}", result.Length);
            return text.ToString();
        }
    }
}
