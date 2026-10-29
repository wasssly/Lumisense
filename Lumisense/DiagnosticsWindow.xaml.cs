using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Lumisense;

// Окно встроенной диагностики: постоянное наблюдение, быстрая проверка, замер по сценарию (как скрипт замеров, но из плеера)
// и долгая проверка утечек с автоматической сменой треков.
internal partial class DiagnosticsWindow : FluentWindow
{
    private const double WatchWarmupMinutes = 30;

    private sealed record RunOutcome(IReadOnlyList<ResourceSample> Samples, int InputDetections, string? Failure);

    private readonly IDiagnosticsHost _host;
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _cts;
    private IReadOnlyList<ResourceSample> _lastSamples = Array.Empty<ResourceSample>();
    private string _lastReport = string.Empty;

    public DiagnosticsWindow(AppSettings settings, IDiagnosticsHost host)
    {
        _host = host;
        InitializeComponent();
        AccessibilityPreferences.ApplyToWindow(this, settings);
        LocalizationService.Apply(this);

        foreach (ResourceScenario scenario in ResourceScenarios.All)
        {
            ScenarioCombo.Items.Add(new ComboBoxItem
            {
                Tag = scenario.Key,
                Content = LocalizationService.Translate(scenario.Title)
            });
        }

        ScenarioCombo.SelectedIndex = 0;

        _watchTimer.Tick += (_, _) => RefreshWatch();
        Loaded += (_, _) =>
        {
            RefreshWatch();
            _watchTimer.Start();
        };
        Closed += (_, _) =>
        {
            _watchTimer.Stop();
            _cts?.Cancel();
        };
    }

    private bool IsRunning => _cts is not null;

    private void RefreshWatch()
    {
        ResourceWatchdog? watchdog = ResourceWatchdog.Current;
        if (watchdog is null)
        {
            WatchText.Text = string.Empty;
            return;
        }

        IReadOnlyList<ResourceSample> samples = watchdog.Samples;
        ResourceSample latest = samples[^1];
        ResourceSummary summary = ResourceTrend.Summarize(samples, WatchWarmupMinutes);
        ResourceAssessment assessment = ResourceTrend.Assess(summary);

        var lines = new List<string>
        {
            LocalizationService.Format("Наблюдение идёт {0} мин. Сейчас: закрытая память {1} МиБ, дескрипторы {2}, GDI-объекты {3}, USER-объекты {4}.",
                Math.Round(latest.Minute), ResourceTrend.Format(latest.PrivateMiB), latest.Handles, latest.GdiObjects, latest.UserObjects)
        };

        switch (assessment.Verdict)
        {
            case ResourceVerdict.SuspectedLeak:
                lines.Add(LocalizationService.Translate("Вывод: возможна утечка"));
                lines.AddRange(assessment.Reasons.Select(reason => "  - " + reason));
                break;
            case ResourceVerdict.Normal:
                lines.Add(LocalizationService.Translate("Вывод: признаков утечки нет"));
                break;
            default:
                lines.Add(LocalizationService.Translate("Для вывода об утечках нужно проработать не менее 50 минут."));
                break;
        }

        WatchText.Text = string.Join(Environment.NewLine, lines);
    }

    private async void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        var session = new ResourceSession
        {
            Title = LocalizationService.Translate("Проверка нагрузки"),
            Duration = TimeSpan.FromSeconds(60),
            SampleInterval = TimeSpan.FromSeconds(1),
            LoadMode = true,
            TrackUserInput = true
        };
        await RunAsync(
            session.Title, warmupMinutes: 0, loadMode: true, QuickButton, minimize: false,
            status: LocalizationService.Translate("Идёт проверка…"),
            async (progress, token) =>
            {
                IReadOnlyList<ResourceSample> samples = await session.RunAsync(null, progress, token);
                return new RunOutcome(samples, session.InputDetections, null);
            });
    }

    private async void ScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        string key = (ScenarioCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        ResourceScenario? scenario = ResourceScenarios.All.FirstOrDefault(item => item.Key == key);
        if (scenario is null) return;

        string title = LocalizationService.Translate(scenario.Title);
        System.Windows.MessageBoxResult answer = LocalizedMessageBox.Show(
            LocalizationService.Format("Начать замер «{0}»? Плеер сам переключит режим окна и воспроизведение и вернёт всё как было. На время замера (около 70 секунд) не двигайте мышь, не нажимайте клавиши и не запускайте другие программы. Окно диагностики свернётся и вернётся с результатом.", title),
            LocalizationService.Translate("Замер по сценарию"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information,
            System.Windows.MessageBoxResult.Yes);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        await RunAsync(
            title, warmupMinutes: 0, loadMode: true, ScenarioButton, minimize: true,
            status: LocalizationService.Translate("Подготовка: переключаю режим и жду 8 секунд, затем замер…"),
            async (progress, token) =>
            {
                ScenarioResult result = await ResourceScenarioRunner.RunAsync(scenario, _host, progress, token);
                return new RunOutcome(result.Samples, result.InputDetections, result.FailureReason);
            });
    }

    private async void LeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        int minutes = int.Parse((LeakDurationCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "60");
        bool autoNext = AutoNextCheckBox.IsChecked == true;
        var session = new ResourceSession
        {
            Title = LocalizationService.Translate("Проверка утечек памяти"),
            Duration = TimeSpan.FromMinutes(minutes),
            SampleInterval = TimeSpan.FromSeconds(30),
            NextTrackInterval = autoNext ? TimeSpan.FromSeconds(20) : TimeSpan.Zero,
            WarmupMinutes = Math.Min(10, minutes * 0.2)
        };
        await RunAsync(
            session.Title, session.WarmupMinutes, loadMode: false, LeakButton, minimize: false,
            status: LocalizationService.Translate("Идёт проверка…"),
            async (progress, token) =>
            {
                IReadOnlyList<ResourceSample> samples = await session.RunAsync(autoNext ? _host.NextTrack : null, progress, token);
                return new RunOutcome(samples, 0, null);
            });
    }

    // Общая обвязка окна для любой проверки: блокирует остальные кнопки, показывает ход, собирает отчёт.
    private async Task RunAsync(
        string title, double warmupMinutes, bool loadMode, Wpf.Ui.Controls.Button runButton, bool minimize, string status,
        Func<IProgress<(double Fraction, ResourceSample Last)>, CancellationToken, Task<RunOutcome>> work)
    {
        _cts = new CancellationTokenSource();
        Wpf.Ui.Controls.Button[] all = { QuickButton, ScenarioButton, LeakButton };
        foreach (Wpf.Ui.Controls.Button button in all) button.IsEnabled = button == runButton;
        runButton.Content = LocalizationService.Translate("Остановить");
        CopyButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ResultBox.Text = string.Empty;
        RunProgress.Value = 0;
        RunProgress.Visibility = Visibility.Visible;
        StatusText.Text = status;

        var progress = new Progress<(double Fraction, ResourceSample Last)>(update =>
        {
            RunProgress.Value = update.Fraction * 100;
            StatusText.Text = LocalizationService.Format("Идёт проверка: {0}%, закрытая память {1} МиБ, смен трека {2}",
                Math.Round(update.Fraction * 100), ResourceTrend.Format(update.Last.PrivateMiB), update.Last.TrackChanges);
        });

        if (minimize)
            WindowState = WindowState.Minimized;

        RunOutcome outcome = new(Array.Empty<ResourceSample>(), 0, null);
        bool cancelled = false;
        try
        {
            outcome = await work(progress, _cts.Token);
            cancelled = _cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // Проверка вспомогательная: сбой в ней не должен ронять плеер.
            Logger.Error("Сбой встроенной диагностики", ex);
            outcome = new RunOutcome(Array.Empty<ResourceSample>(), 0, LocalizationService.Translate("Проверка прервана из-за ошибки"));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        if (minimize)
        {
            WindowState = WindowState.Normal;
            Activate();
        }

        runButton.Content = LocalizationService.Translate(runButton == LeakButton ? "Запустить проверку" : runButton == ScenarioButton ? "Запустить замер" : "Запустить проверку");
        foreach (Wpf.Ui.Controls.Button button in all) button.IsEnabled = true;
        RunProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = outcome.Failure ?? LocalizationService.Translate(cancelled ? "Проверка остановлена" : "Проверка завершена");

        _lastSamples = outcome.Samples;
        _lastReport = outcome.Samples.Count == 0
            ? string.Empty
            : ResourceReport.Build(title, outcome.Samples, warmupMinutes, loadMode, outcome.InputDetections);
        ResultBox.Text = _lastReport;
        CopyButton.IsEnabled = SaveButton.IsEnabled = _lastReport.Length > 0;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport.Length == 0) return;

        try
        {
            System.Windows.Clipboard.SetText(_lastReport);
            StatusText.Text = LocalizationService.Translate("Отчёт скопирован");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Буфер обмена может быть занят другой программой: пользователь просто нажмёт ещё раз.
            StatusText.Text = LocalizationService.Translate("Не удалось скопировать: буфер обмена занят");
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport.Length == 0) return;

        try
        {
            string directory = ResourceReport.Save(_lastReport, _lastSamples);
            StatusText.Text = LocalizationService.Format("Отчёт сохранён: {0}", directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось сохранить отчёт диагностики: {ex.Message}");
            StatusText.Text = LocalizationService.Translate("Не удалось сохранить отчёт");
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense", "diagnostics");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true })?.Dispose();
    }
}
