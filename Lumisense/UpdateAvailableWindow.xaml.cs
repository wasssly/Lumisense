using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
    private readonly bool _isMsiMigrationOnly;

    public UpdateAvailableWindow(UpdateCheckResult result, AppSettings? settings = null)
    {
        InitializeComponent();

        _result = result;
        _settings = settings;
        _isMsiMigrationOnly = result.Status == UpdateCheckStatus.MsiMigrationAvailable;

        ApplyMigrationPresentation();
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;

        string releaseNotes = FormatReleaseNotes(result.ReleaseNotes);
        if (!string.IsNullOrWhiteSpace(releaseNotes))
        {
            NotesText.Text = releaseNotes;
        }
        else
        {
            NotesText.Visibility = Visibility.Collapsed;
        }

        MoreButton.Visibility = string.IsNullOrEmpty(result.ReleaseNotesUrl) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        ApplyMigrationPresentation();
    }

    // В migration-only режиме текущая EXE-копия уже совпадает с последним release. Поэтому
    // не называем его «обновлением» и не предлагаем повторно скачать тот же EXE — оставляем
    // только добровольный, явно обозначенный переход на проверенный MSI.
    private void ApplyMigrationPresentation()
    {
        if (_isMsiMigrationOnly)
        {
            DialogTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateMsiMigrationAvailableTitle);
            VersionsText.Text = LocalizationService.FormatKey(
                LocalizationKey.UpdateMsiMigrationCurrentVersion, _result.CurrentVersion);
            NotesText.Visibility = Visibility.Collapsed;
            NotesScrollViewer.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Collapsed;
            LaterButton.Content = LocalizationService.Get(LocalizationKey.UpdateMsiMigrationClose);
        }
        else
        {
            VersionsText.Text = LocalizationService.Translate(
                $"Версия {_result.LatestVersion} (у вас {_result.CurrentVersion})");
        }

        bool canOfferMsiMigration = _result.DeliveryKind == UpdateDeliveryKind.LegacyInnoSetup &&
                                    !string.IsNullOrWhiteSpace(_result.MsiDownloadUrl) &&
                                    !string.IsNullOrWhiteSpace(_result.MsiSha256);
        MsiMigrationPanel.Visibility = canOfferMsiMigration ? Visibility.Visible : Visibility.Collapsed;
        MigrateToMsiButton.Visibility = canOfferMsiMigration ? Visibility.Visible : Visibility.Collapsed;
        if (canOfferMsiMigration)
        {
            MsiMigrationHintText.Text = LocalizationService.Get(LocalizationKey.UpdateMsiMigrationHint);
            MigrateToMsiButton.Content = LocalizationService.Get(LocalizationKey.UpdateMsiMigrationButton);
        }
    }

    // GitHub Release body приходит в Markdown, но TextBlock не умеет его рендерить и показывал
    // пользователю служебные символы (#, **, [ссылка](url)). Для компактного диалога обновления
    // нужен не полноценный HTML/Markdown-движок, а безопасное плоское представление: заголовки,
    // маркеры и callout-блоки становятся обычным читаемым текстом, а ссылки отображаются подписью.
    private static string FormatReleaseNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var lines = new List<string>();
        bool inCodeBlock = false;

        foreach (string rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (!inCodeBlock && trimmed.StartsWith('>'))
            {
                trimmed = trimmed[1..].TrimStart();
            }

            if (!inCodeBlock && trimmed.StartsWith("[!IMPORTANT]", StringComparison.OrdinalIgnoreCase))
            {
                AddBlankLineBeforeSection(lines);
                lines.Add(LocalizationService.Get(LocalizationKey.UpdateImportant));
                continue;
            }

            if (!inCodeBlock)
            {
                int headingLength = 0;
                while (headingLength < trimmed.Length && trimmed[headingLength] == '#') headingLength++;

                if (headingLength > 0 && headingLength < trimmed.Length && char.IsWhiteSpace(trimmed[headingLength]))
                {
                    AddBlankLineBeforeSection(lines);
                    trimmed = trimmed[headingLength..].TrimStart();
                }
                else if (trimmed is "---" or "***" or "___")
                {
                    AddBlankLineBeforeSection(lines);
                    continue;
                }
                else if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("+ ", StringComparison.Ordinal))
                {
                    trimmed = "• " + trimmed[2..];
                }
            }

            trimmed = Regex.Replace(trimmed, @"\[([^\]]+)\]\([^)]+\)", "$1");
            trimmed = trimmed.Replace("**", string.Empty)
                             .Replace("__", string.Empty)
                             .Replace("~~", string.Empty)
                             .Replace("`", string.Empty);

            lines.Add(trimmed);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddBlankLineBeforeSection(List<string> lines)
    {
        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add(string.Empty);
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        // Запоминаем именно эту версию, а не факт "обновление отклонили вообще" — как только
        // выйдет более новая, диалог на старте снова появится сам.
        if (!_isMsiMigrationOnly && _settings != null && _result.LatestVersion != null)
        {
            _settings.SkippedUpdateVersion = _result.LatestVersion;
            SettingsManager.Save(_settings);
        }

        Close();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_result.ReleaseNotesUrl) ||
            !UpdateChecker.IsTrustedReleaseNotesUrl(_result.ReleaseNotesUrl)) return;

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

        if (_result.DeliveryKind == UpdateDeliveryKind.Velopack)
            await InstallViaVelopackAsync();
        else
            await InstallViaExeAsync();
    }

    private async void MigrateToMsiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        if (string.IsNullOrWhiteSpace(_result.MsiDownloadUrl) || string.IsNullOrWhiteSpace(_result.MsiSha256))
        {
            ShowError(LocalizationService.Get(LocalizationKey.UpdateMsiMigrationUnavailable));
            return;
        }

        var confirmation = LocalizedMessageBox.Show(
            this,
            LocalizationService.Get(LocalizationKey.UpdateMsiMigrationConfirmMessage),
            LocalizationService.Get(LocalizationKey.UpdateMsiMigrationConfirmTitle),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information,
            System.Windows.MessageBoxResult.No);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        SetDownloading(true);
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressInfo>(UpdateDownloadProgressUi);

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(_result.MsiDownloadUrl, source);
            string msiPath = await UpdateChecker.DownloadMsiAsync(downloadUrl, _result.MsiSha256, progress, _downloadCts.Token);

            SetPreparingForMsiMigration();
            UpdateChecker.LaunchMsiAndExit(msiPath, _result.MsiSha256);
        }
        catch (OperationCanceledException)
        {
            SetDownloading(false);
        }
        catch (Exception ex)
        {
            SetDownloading(false);
            Logger.Warn($"Не удалось скачать MSI для перехода: {ex.Message}");
            ShowError($"{LocalizationService.Get(LocalizationKey.UpdateMsiMigrationDownloadFailed)}\n\n{ex.Message}");
        }
    }

    // Velopack сам выбирает delta или full package, скачивает его с верификацией и только
    // после успешной подготовки получает разрешение закрыть приложение и перезапустить его.
    // Этот путь недоступен legacy Inno Setup-установкам: проверка DeliveryKind происходит выше.
    private async Task InstallViaVelopackAsync()
    {
        if (_result.VelopackUpdate is null)
        {
            ShowError(LocalizationService.Get(LocalizationKey.UpdateVelopackUnavailable));
            return;
        }

        SetDownloading(true);
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<int>(UpdateVelopackProgressUi);

        try
        {
            var service = new VelopackUpdateService();
            await service.DownloadAsync(_result.VelopackUpdate, progress, _downloadCts.Token);

            SetPreparing(isVelopack: true);
            // При успехе Update.exe завершит этот процесс, применит уже проверенный package
            // и запустит Lumisense заново. Если метод бросит исключение, UI останется живым.
            service.ApplyAndRestart(_result.VelopackUpdate);
        }
        catch (OperationCanceledException)
        {
            SetDownloading(false);
        }
        catch (Exception ex)
        {
            SetDownloading(false);
            ShowError($"{LocalizationService.Get(LocalizationKey.UpdateVelopackUnavailable)}\n{ex.Message}");
        }
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

        string expectedSha256 = _result.InstallerSha256 ?? "";
        if (string.IsNullOrEmpty(expectedSha256))
        {
            ShowError("В GitHub Release отсутствует контрольная сумма SHA-256 для установщика.");
            return;
        }

        SetDownloading(true);

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressInfo>(UpdateDownloadProgressUi);

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(_result.DownloadUrl, source);

            string exePath = await UpdateChecker.DownloadInstallerAsync(
                downloadUrl, expectedSha256, progress, _downloadCts.Token);

            // Перед запуском самого установщика тоже показываем "Подготовка…" — по сути пауза
            // тут почти нулевая (просто передать управление Process.Start), но без этой фазы
            // прогресс-бар так же "зависал" бы на 100% на те доли секунды, что окно ещё
            // остаётся открытым.
            SetPreparing(isVelopack: false);

            UpdateChecker.LaunchInstallerAndExit(exePath, expectedSha256);
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
        InstallButton.Content = isDownloading
            ? LocalizationService.Translate("Отмена")
            : LocalizationService.Translate("Скачать и установить");
        InstallButton.Appearance = isDownloading ? ControlAppearance.Secondary : ControlAppearance.Primary;
        LaterButton.IsEnabled = !isDownloading;
        MoreButton.IsEnabled = !isDownloading;
        MigrateToMsiButton.IsEnabled = !isDownloading;
        DownloadProgressBar.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        // Неопределённый — пока не пришёл первый отчёт о прогрессе с известным общим размером
        // (см. UpdateDownloadProgressUi); пустая полоса на 0% в первые доли секунды скачивания
        // выглядела как зависание сильнее, чем честная "думающая" анимация.
        DownloadProgressBar.IsIndeterminate = isDownloading;
        DownloadProgressBar.Value = 0;
        PhaseText.Text = isDownloading
            ? (_result.DeliveryKind == UpdateDeliveryKind.Velopack
                ? LocalizationService.Get(LocalizationKey.UpdateVelopackDownload)
                : LocalizationService.Translate("Скачивание…"))
            : "";
        PhaseText.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
    }

    // UpdateManager сообщает нормализованный процент для full/delta-цепочки, но не обещает
    // размер байтов. Показываем честный процент вместо выдуманного размера delta-пакета.
    private void UpdateVelopackProgressUi(int percentage)
    {
        int normalized = Math.Clamp(percentage, 0, 100);
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = normalized / 100d;
        PhaseText.Text = $"{LocalizationService.Get(LocalizationKey.UpdateVelopackDownload)} {normalized}%";
        PhaseText.Visibility = Visibility.Visible;
    }

    // Показывает размер, процент и текущую скорость скачивания legacy Inno Setup установщика.
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

    // Короткая фаза передачи управления выбранному updater после завершения скачивания.
    private void SetPreparingForMsiMigration()
    {
        DownloadProgressBar.IsIndeterminate = true;
        PhaseText.Text = LocalizationService.Get(LocalizationKey.UpdateMsiMigrationLaunching);
        PhaseText.Visibility = Visibility.Visible;
    }

    private void SetPreparing(bool isVelopack)
    {
        DownloadProgressBar.IsIndeterminate = true;
        PhaseText.Text = isVelopack
            ? LocalizationService.Get(LocalizationKey.UpdateVelopackApplying)
            : LocalizationService.Translate("Запуск установщика…");
        PhaseText.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }
}
