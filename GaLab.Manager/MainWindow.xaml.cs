using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GaLab.Shared;
using TspLibrary;

namespace GaLab.Manager;

public partial class MainWindow : Window
{
    private readonly HttpClient httpClient = new();
    private readonly GaManagerClient managerClient;
    private CancellationTokenSource? cancellation;
    private bool closeRequested;
    private readonly Dictionary<string, ServiceSnapshot> serviceStates = [];
    private double[,]? currentDistances;
    private GenerationPoint[] graphHistory = [];

    public MainWindow()
    {
        InitializeComponent();
        // Each request has its own 5-minute timeout in GaManagerClient.
        // Disable HttpClient's shorter built-in default (100 seconds).
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        managerClient = new GaManagerClient(httpClient);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRequest(out SolveRequest? request, out double[,]? distances))
            return;
        if (!ReadInteger(JobsPerServiceBox, "Заданий на сервис", 1, 1000, out int jobsPerService))
            return;
        string[] urls = ServiceUrlsBox.Text.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length == 0)
        {
            ShowInputError(ServiceUrlsBox, "Укажите хотя бы один сервис.");
            return;
        }
        ResultBox.Clear();
        WarningsBox.Text = "Предупреждений нет.";
        serviceStates.Clear();
        ServicesBox.Text = "Проверка доступности сервисов...";
        graphHistory = [];
        GraphCanvas.Children.Clear();
        currentDistances = distances;
        closeRequested = false;
        cancellation = new CancellationTokenSource();
        SetRunning(true);
        StatusText.Text = "Задания отправлены вычислительным сервисам...";
        try
        {
            var monitoring = new Progress<ServiceSnapshot>(snapshot =>
            {
                serviceStates[snapshot.ServiceUrl] = snapshot;
                ServicesBox.Text = string.Join(Environment.NewLine,
                    serviceStates.OrderBy(pair => pair.Key).Select(pair =>
                    {
                        ServiceSnapshot state = pair.Value;
                        string generation = state.TotalGenerations > 0
                            ? $" — поколение {state.Generation}/{state.TotalGenerations}"
                            : string.Empty;
                        return $"{pair.Key}: {state.State}{generation}, готово заданий: {state.CompletedJobs}" +
                            (state.Detail is null ? string.Empty : $" ({state.Detail})");
                    }));
            });
            SolveResponse response = await managerClient.SolveDynamicallyAsync(
                urls, request!, cancellation.Token, monitoring, jobsPerService: jobsPerService);
            ResultBox.Text = ProblemData.FormatResult(
                new GenerationResult(response.Generation, response.Route, response.Length), currentDistances!);
            graphHistory = response.History;
            DrawGraph();
            ShowWarnings();
            StatusText.Text = managerClient.LastWarnings.Count == 0
                ? "Поиск завершён: показан лучший маршрут среди сервисов."
                : "Поиск завершён с предупреждениями: показан лучший успешный маршрут.";
        }
        catch (OperationCanceledException)
        {
            ShowWarnings();
            StatusText.Text = "Отправлена отмена запросов. Промежуточный маршрут в этой версии не возвращается.";
        }
        catch (Exception exception)
        {
            ShowWarnings();
            StatusText.Text = "Не удалось получить результат.";
            MessageBox.Show(this, exception.Message, "Ошибка сервисов",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
            SetRunning(false);
            if (closeRequested)
                Close();
        }
    }

    private bool TryReadRequest(out SolveRequest? request, out double[,]? distances)
    {
        request = null;
        distances = null;
        if (!ReadInteger(CitiesBox, "Число городов", 3, 200, out int cities) ||
            !ReadInteger(PopulationBox, "Популяция", 2, 1_000_000, out int population) ||
            !ReadInteger(GenerationsBox, "Число поколений", 1, 1_000_000, out int generations) ||
            !ReadProbability(MutationRateBox, "Вероятность мутации", out double mutation) ||
            !ReadInteger(CitySeedBox, "Seed городов", int.MinValue, int.MaxValue, out int citySeed))
            return false;
        int? algorithmSeed = null;
        if (AlgorithmSeedBox.Text.Trim().Length > 0)
        {
            if (!int.TryParse(AlgorithmSeedBox.Text.Trim(), out int parsed))
            {
                ShowInputError(AlgorithmSeedBox, "Seed алгоритма должен быть целым или пустым.");
                return false;
            }
            algorithmSeed = parsed;
        }
        // Coordinates/distances are generated ONCE. All workers receive this exact matrix.
        distances = ProblemData.CreateDistances(cities, citySeed);
        double[][] matrix = new double[cities][];
        for (int i = 0; i < cities; i++)
        {
            matrix[i] = new double[cities];
            for (int j = 0; j < cities; j++)
                matrix[i][j] = distances[i, j];
        }
        request = new SolveRequest(matrix, population, generations, mutation, algorithmSeed);
        return true;
    }

    private static bool ReadInteger(TextBox box, string name, int min, int max, out int value)
    {
        if (int.TryParse(box.Text, out value) && value >= min && value <= max)
            return true;
        MessageBox.Show(name + ": введите целое число от " + min + " до " + max + ".", "Проверка ввода");
        box.Focus(); box.SelectAll();
        return false;
    }

    private static bool ReadProbability(TextBox box, string name, out double value)
    {
        string text = box.Text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) && value >= 0 && value <= 1)
            return true;
        MessageBox.Show(name + ": введите число от 0 до 1.", "Проверка ввода");
        box.Focus(); box.SelectAll();
        return false;
    }

    private void ShowInputError(TextBox input, string message)
    {
        MessageBox.Show(this, message, "Проверка ввода", MessageBoxButton.OK, MessageBoxImage.Information);
        input.Focus(); input.SelectAll();
    }

    private void DrawGraph()
    {
        GraphCanvas.Children.Clear();
        if (graphHistory.Length == 0 || GraphCanvas.ActualWidth < 30 || GraphCanvas.ActualHeight < 30)
            return;
        double width = GraphCanvas.ActualWidth - 25;
        double height = GraphCanvas.ActualHeight - 22;
        double min = graphHistory.Min(p => p.Length);
        double max = graphHistory.Max(p => p.Length);
        if (max - min < 1e-9) max = min + 1;
        int minGen = graphHistory[0].Generation;
        int maxGen = graphHistory[^1].Generation;
        double span = Math.Max(1, maxGen - minGen);
        Polyline line = new() { Stroke = Brushes.SteelBlue, StrokeThickness = 2 };
        foreach (GenerationPoint p in graphHistory)
        {
            double x = 10 + (p.Generation - minGen) / span * width;
            double y = 10 + (max - p.Length) / (max - min) * height;
            line.Points.Add(new Point(x, y));
        }
        GraphCanvas.Children.Add(line);
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    private void ApplyServicesButton_Click(object sender, RoutedEventArgs e)
    {
        string[] urls = ServiceUrlsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        try
        {
            managerClient.UpdateServices(urls);
            StatusText.Text = urls.Length == 0
                ? "Сервисы отключены. Для продолжения добавьте адреса и примените изменения."
                : "Список вычислительных сервисов обновлён.";
        }
        catch (ArgumentException exception)
        {
            ShowInputError(ServiceUrlsBox, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Ошибка сервисов",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ApplyServicesButton.IsEnabled = running;
        foreach (Control control in new Control[] {
            CitiesBox, PopulationBox, GenerationsBox,
            MutationRateBox, CitySeedBox, AlgorithmSeedBox, JobsPerServiceBox })
            control.IsEnabled = !running;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "Остановка сетевых запросов...";
    }

    private void ShowWarnings()
    {
        WarningsBox.Text = managerClient.LastWarnings.Count == 0
            ? "Предупреждений нет."
            : string.Join(Environment.NewLine, managerClient.LastWarnings);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (cancellation is not null)
        {
            e.Cancel = true;
            closeRequested = true;
            cancellation.Cancel();
            StopButton.IsEnabled = false;
        }
    }

    private void Window_Closed(object sender, EventArgs e) => httpClient.Dispose();
}
