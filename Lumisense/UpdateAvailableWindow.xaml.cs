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

        // Выбор способа установки виден, только если у релиза реально есть оба варианта — если
        // только один, показывать не из чего выбирать (см. также подробный комментарий в XAML
        // у InstallMethodPanel). Если нет ни одного — InstallButton_Click сам покажет понятную
        // ошибку при попытке нажать "Установить", отдельно предупреждать здесь не нужно.
        bool hasBoth = !string.IsNullOrEmpty(result.ZipDownloadUrl) && !string.IsNullOrEmpty(result.ExeDownloadUrl);
        InstallMethodPanel.Visibility = hasBoth ? Visibility.Visible : Visibility.Collapsed;

        // Если доступен только .exe (например, релиз собран без ZIP) — сразу выставляем этот
        // единственный вариант, а не оставляем радиокнопку "ZIP" выбранной по умолчанию
        // молча указывающей на несуществующий файл.
        if (string.IsNullOrEmpty(result.ZipDownloadUrl) && !string.IsNullOrEmpty(result.ExeDownloadUrl))
            InstallMethodExeRadio.IsChecked = true;
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
        // Тот же выбор, что был бы виден в InstallMethodPanel, если бы оба варианта были
        // доступны — если панель скрыта (доступен только один вариант), IsChecked радиокнопок
        // уже выставлен на единственно возможный (см. конструктор), так что читать его отсюда
        // безопасно в любом случае.
        bool useExe = InstallMethodExeRadio.IsChecked == true;

        if (useExe)
        {
            await InstallViaExeAsync();
        }
        else
        {
            await InstallViaZipAsync();
        }
    }

    private async Task InstallViaZipAsync()
    {
        if (string.IsNullOrEmpty(_result.ZipDownloadUrl))
        {
            ShowError("Не удалось найти ZIP-архив обновления в этом релизе.");
            return;
        }

        SetDownloading(true);

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<UpdateChecker.DownloadProgressInfo>(UpdateDownloadProgressUi);

        // Сессионная временная папка на эту конкретную попытку обновления — сюда попадёт и
        // сам скачанный ZIP, и распакованные из него файлы (см. UpdateChecker.CreateUpdateSession).
        string sessionDir = UpdateChecker.CreateUpdateSession();

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(_result.ZipDownloadUrl, source);

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

    // Второй способ установки — скачать .exe-установщик и запустить его, вместо распаковки
    // ZIP через Updater. Проще ZIP-варианта: готовить и запускать отдельный процесс-помощник
    // не нужно, сам установщик уже умеет закрыть плеер, заменить файлы и предложить запуск
    // обновлённой версии — ровно то же самое, что он делает при обычной первой установке.
    private async Task InstallViaExeAsync()
    {
        if (string.IsNullOrEmpty(_result.ExeDownloadUrl))
        {
            ShowError("Не удалось найти .exe-установщик в этом релизе.");
            return;
        }

        SetDownloading(true);

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<UpdateChecker.DownloadProgressInfo>(UpdateDownloadProgressUi);

        string sessionDir = UpdateChecker.CreateUpdateSession();

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(_result.ExeDownloadUrl, source);

            string exePath = await UpdateChecker.DownloadUpdateExeAsync(downloadUrl, sessionDir, progress, _downloadCts.Token);

            // Перед запуском самого установщика тоже показываем "Подготовка…" — по сути пауза
            // тут почти нулевая (просто передать управление Process.Start), но без этой фазы
            // прогресс-бар так же "зависал" бы на 100% на те доли секунды, что окно ещё
            // остаётся открытым.
            SetPreparing();

            // В отличие от ZIP-варианта, sessionDir с самим .exe-установщиком НЕ удаляем —
            // установщик ещё не запустился и не закончил работу к моменту завершения этого
            // метода (UseShellExecute запускает его асинхронно), удалить папку из-под него было
            // бы преждевременно. Здесь это не проблема: один установщик на несколько МБ во
            // временной папке не накопится в сколько-нибудь заметный мусор, в отличие от того,
            // что уже расчищает после себя сам Updater для сценария ZIP.
            UpdateChecker.LaunchInstallerAndExit(exePath);
        }
        catch (Exception ex)
        {
            SetDownloading(false);
            ShowError($"Не удалось скачать установщик: {ex.Message}");
        }
    }

    private void SetDownloading(bool isDownloading)
    {
        InstallButton.IsEnabled = !isDownloading;
        LaterButton.IsEnabled = !isDownloading;
        MoreButton.IsEnabled = !isDownloading;
        InstallMethodZipRadio.IsEnabled = !isDownloading;
        InstallMethodExeRadio.IsEnabled = !isDownloading;
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

    // Живое обновление текста и прогресс-бара во время скачивания (см. UpdateChecker.
    // DownloadProgressInfo/DownloadToFileAsync) — "12,4 МБ из 45,2 МБ (27%) — 3,1 МБ/с", а не
    // голый процент, как было раньше.
    private void UpdateDownloadProgressUi(UpdateChecker.DownloadProgressInfo info)
    {
        bool knowsTotal = info.TotalBytes is > 0;

        DownloadProgressBar.IsIndeterminate = !knowsTotal;
        if (knowsTotal) DownloadProgressBar.Value = info.Fraction;

        string receivedText = FormatBytes(info.BytesReceived);
        string? speedText = info.BytesPerSecond > 0 ? $"{FormatBytes((long)info.BytesPerSecond)}/с" : null;

        string text = knowsTotal
            ? $"Скачивается {receivedText} из {FormatBytes(info.TotalBytes!.Value)} ({info.Fraction:P0})"
            : $"Скачивается {receivedText}";

        if (speedText != null) text += $" — {speedText}";

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
