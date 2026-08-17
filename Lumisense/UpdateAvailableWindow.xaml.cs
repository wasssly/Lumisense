using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace AudioPlayer;

// Модальный диалог "доступно обновление". Когда его показывать (тихо на старте только для
// новой версии или всегда по кнопке в настройках) — решает вызывающая сторона, сам он ничего
// не решает про это.
public partial class UpdateAvailableWindow : FluentWindow
{
    private readonly UpdateCheckResult _result;
    private readonly AppSettings? _settings;
    private CancellationTokenSource? _downloadCts;

    public UpdateAvailableWindow(UpdateCheckResult result, AppSettings? settings = null)
    {
        InitializeComponent();

        _result = result;
        _settings = settings;

        VersionsText.Text = $"Версия {result.LatestVersion} (у вас {result.CurrentVersion})";

        if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            NotesText.Text = result.ReleaseNotes.Trim();
        }
        else
        {
            NotesText.Visibility = Visibility.Collapsed;
        }

        MoreButton.Visibility = string.IsNullOrEmpty(result.ReleaseNotesUrl) ? Visibility.Collapsed : Visibility.Visible;

    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        // Запоминаем именно эту версию, а не факт "обновление отклонили вообще" — как только
        // выйдет более новая, диалог на старте снова появится сам.
        if (_settings != null && _result.LatestVersion != null)
        {
            _settings.SkippedUpdateVersion = _result.LatestVersion;
            SettingsManager.Save(_settings);
        }

        Close();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_result.ReleaseNotesUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(_result.ReleaseNotesUrl) { UseShellExecute = true });
        }
        catch
        {
            // Нет браузера по умолчанию и т.п. — не критично, просто ничего не открылось
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        await InstallViaExeAsync();
    }

    // Скачивает и запускает Inno Setup установщик. Отмена прерывает HTTP-запрос
    // до запуска установщика; после запуска управление передаётся самому установщику.
    private async Task InstallViaExeAsync()
    {
        if (string.IsNullOrEmpty(_result.DownloadUrl))
        {
            ShowError("Не удалось найти .exe-установщик в этом релизе.");
            return;
        }

        SetDownloading(true);

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressInfo>(UpdateDownloadProgressUi);

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(_result.DownloadUrl, source);

            string exePath = await UpdateChecker.DownloadInstallerAsync(downloadUrl, progress, _downloadCts.Token);

            // Перед запуском самого установщика тоже показываем "Подготовка…" — по сути пауза
            // тут почти нулевая (просто передать управление Process.Start), но без этой фазы
            // прогресс-бар так же "зависал" бы на 100% на те доли секунды, что окно ещё
            // остаётся открытым.
            SetPreparing();

            UpdateChecker.LaunchInstallerAndExit(exePath);
        }
        catch (OperationCanceledException)
        {
            SetDownloading(false);
        }
        catch (Exception ex)
        {
            SetDownloading(false);
            ShowError($"Не удалось скачать установщик: {ex.Message}");
        }
    }

    // Отмена переиспользует саму кнопку "Скачать и установить" (меняет подпись/поведение на
    // время скачивания) вместо отдельной кнопки — во время скачивания она и так единственная
    // активная в ряду.
    private bool _isDownloading;

    private void SetDownloading(bool isDownloading)
    {
        _isDownloading = isDownloading;
        InstallButton.Content = isDownloading ? "Отмена" : "Скачать и установить";
        InstallButton.Appearance = isDownloading ? ControlAppearance.Secondary : ControlAppearance.Primary;
        LaterButton.IsEnabled = !isDownloading;
        MoreButton.IsEnabled = !isDownloading;
        DownloadProgressBar.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        // Неопределённый — пока не пришёл первый отчёт о прогрессе с известным общим размером
        // (см. UpdateDownloadProgressUi); пустая полоса на 0% в первые доли секунды скачивания
        // выглядела как зависание сильнее, чем честная "думающая" анимация.
        DownloadProgressBar.IsIndeterminate = isDownloading;
        DownloadProgressBar.Value = 0;
        PhaseText.Text = isDownloading ? "Скачивание…" : "";
        PhaseText.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
    }

    // Показывает размер, процент и текущую скорость скачивания установщика.
    private void UpdateDownloadProgressUi(DownloadProgressInfo info)
    {
        bool knowsTotal = info.TotalBytes is > 0;
        DownloadProgressBar.IsIndeterminate = !knowsTotal;
        if (knowsTotal) DownloadProgressBar.Value = Math.Clamp(info.Fraction, 0, 1);

        string received = FormatBytes(info.BytesReceived);
        string text = knowsTotal
            ? $"Скачивается {received} из {FormatBytes(info.TotalBytes!.Value)} ({info.Fraction:P0})"
            : $"Скачивается {received}";

        if (info.BytesPerSecond > 0)
            text += $" — {FormatBytes((long)info.BytesPerSecond)}/с";

        PhaseText.Text = text;
        PhaseText.Visibility = Visibility.Visible;
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        if (bytes >= mb) return $"{bytes / mb:F1} МБ";
        if (bytes >= kb) return $"{bytes / kb:F0} КБ";
        return $"{bytes} Б";
    }

    // Короткая фаза передачи управления Inno Setup после завершения скачивания.
    private void SetPreparing()
    {
        DownloadProgressBar.IsIndeterminate = true;
        PhaseText.Text = "Запуск установщика…";
        PhaseText.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
