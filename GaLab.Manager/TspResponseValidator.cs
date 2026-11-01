using GaLab.Shared;

namespace GaLab.Manager;

internal static class TspResponseValidator
{
    public static string? Validate(SolveResponse response, SolveRequest request)
    {
        int n = request.Distances.Length;
        if (response.Route is null || response.Route.Length != n)
            return "маршрут неправильной длины";
        var seen = new bool[n];
        foreach (int city in response.Route)
        {
            if (city < 0 || city >= n || seen[city])
                return "маршрут содержит повторяющиеся или отсутствующие города";
            seen[city] = true;
        }
        if (!double.IsFinite(response.Length) || response.Length <= 0)
            return "длина маршрута некорректна";
        if (response.Generation < 0 || response.Generation > request.Generations)
            return "номер поколения некорректен";

        double actualLength = 0;
        for (int i = 0; i < n; i++)
        {
            double segment = request.Distances[response.Route[i]][response.Route[(i + 1) % n]];
            if (!double.IsFinite(segment) || segment <= 0)
                return "матрица расстояний некорректна";
            actualLength += segment;
        }
        if (!double.IsFinite(actualLength))
            return "переполнение при вычислении длины маршрута";
        if (Math.Abs(actualLength - response.Length) > 1e-8 * Math.Max(1.0, actualLength))
            return "указанная длина маршрута не соответствует матрице расстояний";

        if (response.History is null || response.History.Length == 0)
            return "сервис не вернул историю поколений";
        int previous = -1;
        double previousLength = double.PositiveInfinity;
        foreach (GenerationPoint point in response.History)
        {
            if (point is null || point.Generation <= previous || point.Generation > response.Generation ||
                !double.IsFinite(point.Length) || point.Length <= 0 ||
                point.Length > previousLength + 1e-8)
                return "история поколений некорректна";
            previous = point.Generation;
            previousLength = point.Length;
        }
        if (response.History[^1].Generation != response.Generation ||
            Math.Abs(response.History[^1].Length - response.Length) > 1e-8 * Math.Max(1.0, response.Length))
            return "последнее значение истории не совпадает с итогом";
        return null;
    }
}
