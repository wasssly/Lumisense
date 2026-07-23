using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace AudioPlayer;

/// <summary>
/// Модальный диалог "доступно обновление". Использование:
///
///     var result = await UpdateChecker.CheckAsync();
///     if (result.Status == UpdateCheckStatus.UpdateAvailable)
///         new UpdateAvailableWindow(result, settings) { Owner = this }.ShowDialog();
///
/// Сам ничего не решает о том, когда его показывать (тихо на старте только для новой версии,
/// или всегда по кнопке в настройках) — это остаётся на вызывающей стороне.
/// </summary>
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
        if (string.IsNullOrEmpty(_result.DownloadUrl))
        {
            ShowError("Не удалось найти файл установщика в этом релизе.");
            return;
        }

        SetDownloading(true);

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => DownloadProgressBar.Value = p);

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(_result.DownloadUrl, source);

            string installerPath = await UpdateChecker.DownloadInstallerAsync(downloadUrl, progress, _downloadCts.Token);

            // Установщик сам закроет запущенный плеер перед копированием файлов (см.
            // CloseApplications в Lumisense.iss), но выходим сами — так плавнее и без лишнего
            // системного диалога "приложение всё ещё занято".
            UpdateChecker.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            SetDownloading(false);
            ShowError($"Не удалось скачать обновление: {ex.Message}");
        }
    }

    private void SetDownloading(bool isDownloading)
    {
        InstallButton.IsEnabled = !isDownloading;
        LaterButton.IsEnabled = !isDownloading;
        MoreButton.IsEnabled = !isDownloading;
        DownloadProgressBar.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgressBar.Value = 0;
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
