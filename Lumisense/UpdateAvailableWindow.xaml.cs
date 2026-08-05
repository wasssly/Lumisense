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
        if (string.IsNullOrEmpty(_result.DownloadUrl))
        {
            ShowError("Не удалось найти ZIP-архив обновления в этом релизе.");
            return;
        }

        SetDownloading(true);

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => DownloadProgressBar.Value = p);

        // Сессионная временная папка на эту конкретную попытку обновления — сюда попадёт и
        // сам скачанный ZIP, и распакованные из него файлы (см. UpdateChecker.CreateUpdateSession).
        string sessionDir = UpdateChecker.CreateUpdateSession();

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(_result.DownloadUrl, source);

            string zipPath = await UpdateChecker.DownloadUpdateZipAsync(downloadUrl, sessionDir, progress, _downloadCts.Token);

            // Скачивание завершено — дальше распаковка архива и подготовка Updater'а, это
            // быстро, но заметно на глаз (доли секунды — секунды на больших обновлениях), так
            // что показываем отдельную фазу вместо того, чтобы прогресс-бар просто "завис" на
            // 100%.
            SetPreparing();

            string payloadRoot = UpdateChecker.ExtractUpdatePayload(zipPath, sessionDir);
            string updaterRunnerPath = UpdateChecker.PrepareUpdaterRunner(payloadRoot);

            // С этого момента дальнейшую судьбу обновления берёт на себя Updater (отдельный
            // процесс) — если запуск прошёл успешно, плеер должен закрыться, чтобы файлы
            // освободились и Updater мог их заменить (см. UpdateChecker.LaunchUpdaterAndExit).
            UpdateChecker.LaunchUpdaterAndExit(updaterRunnerPath, payloadRoot, sessionDir);
        }
        catch (Exception ex)
        {
            SetDownloading(false);
            ShowError($"Не удалось подготовить обновление: {ex.Message}");
        }
    }

    private void SetDownloading(bool isDownloading)
    {
        InstallButton.IsEnabled = !isDownloading;
        LaterButton.IsEnabled = !isDownloading;
        MoreButton.IsEnabled = !isDownloading;
        DownloadProgressBar.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;
        PhaseText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
    }

    // Между "скачано" и "плеер вот-вот закроется на обновление" — короткая, но заметная пауза
    // на распаковку архива и подготовку Updater'а. Прогресс здесь не в процентах (неизвестно
    // заранее, сколько это займёт), поэтому индикатор становится неопределённым, а рядом
    // появляется поясняющий текст, чтобы это не выглядело зависанием.
    private void SetPreparing()
    {
        DownloadProgressBar.IsIndeterminate = true;
        PhaseText.Text = "Подготовка обновления…";
        PhaseText.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
