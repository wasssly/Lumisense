using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace Lumisense;

// Модальный диалог "доступно обновление". Когда его показывать (тихо на старте только для
// новой версии или всегда по кнопке в настройках) — решает вызывающая сторона, сам он ничего
// не решает про это.
public partial class UpdateAvailableWindow : FluentWindow
{
    private readonly UpdateCheckResult _result;
    private readonly AppSettings? _settings;
    private CancellationTokenSource? _downloadCts;
    private DownloadPauseController? _pauseController;
    private bool _isVelopackDownload;
    private bool _isVelopackPaused;
    // Смена источника никогда не смешивает байты разных зеркал: текущий запрос отменяется,
    // его .part удаляется сетевым слоем, а новый полный запрос начинается только после этого.
    private bool _restartLegacyDownloadFromNewSource;
    private readonly bool _isMsiMigrationOnly;
    private readonly VelopackUpdateDiagnostics? _velopackDiagnostics;

    public UpdateAvailableWindow(UpdateCheckResult result, AppSettings? settings = null)
    {
        InitializeComponent();

        _result = result;
        _settings = settings;
        _isMsiMigrationOnly = result.Status == UpdateCheckStatus.MsiMigrationAvailable;
        _velopackDiagnostics = result.DeliveryKind == UpdateDeliveryKind.Velopack && result.VelopackUpdate is not null
            ? new VelopackUpdateDiagnostics(result.CurrentVersion, result.VelopackUpdate)
            : null;
        _velopackDiagnostics?.RecordPlan();
        if (_settings != null)
            AccessibilityPreferences.ApplyToWindow(this, _settings);

        ApplyMigrationPresentation();
        ApplyVelopackDiagnosticsPresentation();
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Closing += (_, _) =>
        {
            _restartLegacyDownloadFromNewSource = false;
            _downloadCts?.Cancel();
        };
        Closed += (_, _) =>
        {
            LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
            _velopackDiagnostics?.Dispose();
        };

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
        ApplyVelopackDiagnosticsPresentation();
        RefreshDownloadControlLabels();
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

    private void ApplyVelopackDiagnosticsPresentation()
    {
        bool isVelopack = !_isMsiMigrationOnly && _velopackDiagnostics is not null;
        VelopackDiagnosticsPanel.Visibility = isVelopack ? Visibility.Visible : Visibility.Collapsed;
        if (!isVelopack || _velopackDiagnostics is null) return;

        VelopackUpdatePlan plan = _velopackDiagnostics.Plan;
        VelopackDiagnosticsTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackDiagnosticsTitle);
        CopyVelopackDiagnosticsButton.Content = LocalizationService.Get(LocalizationKey.UpdateVelopackCopyDiagnostics);
        OpenVelopackLogsButton.Content = LocalizationService.Get(LocalizationKey.UpdateVelopackOpenLogs);
        VelopackDeliveryText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackPlanDelivery);
        VelopackTelemetryLimitText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackRuntimeTelemetryLimit);

        var lines = new List<string>
        {
            LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackPlanFullPackage,
                plan.FullPackage.FileName,
                VelopackUpdateDiagnostics.FormatBytes(plan.FullPackage.Size))
        };

        if (plan.HasDeltaPlan)
        {
            lines.Add(LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackPlanDeltas,
                plan.DeltaPackages.Count,
                VelopackUpdateDiagnostics.FormatBytes(plan.DeltaBytes)));
            lines.Add(LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackPlanDeltaFiles,
                string.Join(", ", plan.DeltaPackages.Select(asset => asset.FileName))));
            VelopackFallbackText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackPlanFallback);
        }
        else
        {
            lines.Add(LocalizationService.Get(LocalizationKey.UpdateVelopackPlanFullOnly));
            VelopackFallbackText.Text = string.Empty;
        }

        VelopackPlanText.Text = string.Join(Environment.NewLine, lines);
        RefreshVelopackRuntimePresentation();
    }

    // В отличие от legacy EXE, Velopack SDK публично сообщает только этап и нормализованный
    // процент. Показываем их вместе с планом и временем, но не вычисляем фиктивные байты,
    // скорость или фактически выбранный delta/full package.
    private void RefreshVelopackRuntimePresentation()
    {
        if (_velopackDiagnostics is null) return;

        string state = _velopackDiagnostics.Stage switch
        {
            VelopackUpdateStage.Downloading => LocalizationService.Get(LocalizationKey.UpdateVelopackRuntimeDownloading),
            VelopackUpdateStage.Paused => LocalizationService.Get(LocalizationKey.UpdateVelopackRuntimePaused),
            VelopackUpdateStage.PreparingRestart => LocalizationService.Get(LocalizationKey.UpdateVelopackRuntimePreparing),
            VelopackUpdateStage.Cancelled => LocalizationService.Get(LocalizationKey.UpdateVelopackRuntimeCancelled),
            VelopackUpdateStage.Failed => LocalizationService.Get(LocalizationKey.UpdateVelopackRuntimeFailed),
            _ => LocalizationService.Get(LocalizationKey.UpdateVelopackRuntimeReady)
        };

        VelopackRuntimeStateText.Text = LocalizationService.FormatKey(LocalizationKey.UpdateVelopackRuntimeState, state);
        VelopackRuntimeProgressText.Text = _velopackDiagnostics.Stage switch
        {
            VelopackUpdateStage.Paused => LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackRuntimePausedProgress,
                _velopackDiagnostics.ProgressPercentage,
                _velopackDiagnostics.ElapsedText),
            VelopackUpdateStage.PreparingRestart => LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackRuntimePreparedProgress,
                _velopackDiagnostics.ElapsedText),
            _ => LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackRuntimeProgress,
                _velopackDiagnostics.ProgressPercentage,
                _velopackDiagnostics.ElapsedText)
        };
    }

    private void CopyVelopackDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_velopackDiagnostics is null) return;

        try
        {
            Clipboard.SetText(_velopackDiagnostics.CreateReport());
            PhaseText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackDiagnosticsCopied);
            PhaseText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось скопировать диагностику Velopack: {ex.Message}");
        }
    }

    private void OpenVelopackLogsButton_Click(object sender, RoutedEventArgs e) =>
        VelopackUpdateDiagnostics.OpenVelopackLogsFolder();

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

    private async void PauseDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDownloading) return;

        if (_isVelopackDownload)
        {
            if (_isVelopackPaused)
            {
                _isVelopackPaused = false;
                await InstallViaVelopackAsync(isResuming: true);
                return;
            }

            // Velopack 1.2.0 предоставляет отмену, но не API настоящей паузы потока. Остановка
            // безопасна: при продолжении UpdateManager повторно проверяет пакеты и их checksums.
            _isVelopackPaused = true;
            _velopackDiagnostics?.Pause();
            RefreshVelopackRuntimePresentation();
            PauseDownloadButton.IsEnabled = false;
            PhaseText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackDownloadPaused);
            PhaseText.Visibility = Visibility.Visible;
            _downloadCts?.Cancel();
            return;
        }

        if (_pauseController is null) return;
        if (_pauseController.IsPaused)
            _pauseController.Resume();
        else
            _pauseController.Pause();

        RefreshDownloadControlLabels();
        if (_pauseController.IsPaused)
        {
            PhaseText.Text = LocalizationService.Get(LocalizationKey.UpdateDownloadPaused);
            PhaseText.Visibility = Visibility.Visible;
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDownloading) return;

        _restartLegacyDownloadFromNewSource = false;
        if (_isVelopackPaused)
        {
            _isVelopackPaused = false;
            _velopackDiagnostics?.Cancelled();
            SetDownloading(false);
            RefreshVelopackRuntimePresentation();
            return;
        }

        CancelDownloadButton.IsEnabled = false;
        PauseDownloadButton.IsEnabled = false;
        ChangeDownloadSourceButton.IsEnabled = false;
        PhaseText.Text = LocalizationService.Get(LocalizationKey.UpdateDownloadCancelling);
        PhaseText.Visibility = Visibility.Visible;
        _downloadCts?.Cancel();
    }

    private void ChangeDownloadSourceButton_Click(object sender, RoutedEventArgs e)
    {
        // Velopack управляет своими пакетами сам и не предоставляет безопасную замену base URL
        // в середине операции. Для EXE/MSI можно начать новый полный запрос с другого mirror.
        if (!_isDownloading || _isVelopackDownload || _settings is null || _restartLegacyDownloadFromNewSource)
            return;

        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = ChangeDownloadSourceButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        string currentSource = _settings.UpdateDownloadSource;
        foreach (var source in UpdateChecker.DownloadSources)
        {
            string sourceKey = source.Key;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = source.DisplayName,
                IsCheckable = true,
                IsChecked = string.Equals(sourceKey, currentSource, StringComparison.Ordinal)
            };
            item.Click += (_, _) => RestartLegacyDownloadFromSource(sourceKey);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void RestartLegacyDownloadFromSource(string sourceKey)
    {
        if (!_isDownloading || _isVelopackDownload || _settings is null ||
            string.Equals(_settings.UpdateDownloadSource, sourceKey, StringComparison.Ordinal))
            return;

        string currentSourceName = GetSelectedDownloadSourceDisplayName();
        string newSourceName = GetDownloadSourceDisplayName(sourceKey);
        var confirmation = LocalizedMessageBox.Show(
            this,
            LocalizationService.FormatKey(
                LocalizationKey.UpdateDownloadSourceChangeConfirmMessage, currentSourceName, newSourceName),
            LocalizationService.Get(LocalizationKey.UpdateDownloadSourceChangeConfirmTitle),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirmation != System.Windows.MessageBoxResult.Yes)
            return;

        // Сохраняем выбор сразу, как и в настройках. Следующая операция и последующие обновления
        // используют новый источник; текущий .part не переиспользуется между разными URL.
        _settings.UpdateDownloadSource = sourceKey;
        SettingsManager.Save(_settings);
        _restartLegacyDownloadFromNewSource = true;
        PauseDownloadButton.IsEnabled = false;
        ChangeDownloadSourceButton.IsEnabled = false;
        PhaseText.Text = LocalizationService.FormatKey(
            LocalizationKey.UpdateDownloadSourceChanging, GetSelectedDownloadSourceDisplayName());
        PhaseText.Visibility = Visibility.Visible;
        _downloadCts?.Cancel();
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

        await DownloadAndLaunchLegacyAssetAsync(isMsi: true);
    }

    // Velopack сам выбирает delta или full package, скачивает его с верификацией и только
    // после успешной подготовки получает разрешение закрыть приложение и перезапустить его.
    // Этот путь недоступен legacy Inno Setup-установкам: проверка DeliveryKind происходит выше.
    private async Task InstallViaVelopackAsync(bool isResuming = false)
    {
        if (_result.VelopackUpdate is null)
        {
            ShowError(LocalizationService.Get(LocalizationKey.UpdateVelopackUnavailable));
            return;
        }

        if (isResuming)
            Logger.Info("Пользователь продолжил остановленную загрузку Velopack-обновления.");

        _isVelopackDownload = true;
        _velopackDiagnostics?.Start(isResuming);
        SetDownloading(true);
        VelopackDiagnosticsPanel.IsExpanded = true;
        RefreshVelopackRuntimePresentation();
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<int>(UpdateVelopackProgressUi);

        try
        {
            var service = new VelopackUpdateService();
            await service.DownloadAsync(_result.VelopackUpdate, progress, _downloadCts.Token);

            _velopackDiagnostics?.Prepared();
            SetPreparing(isVelopack: true);
            RefreshVelopackRuntimePresentation();

            // До ApplyAndRestart синхронно фиксируем уже существующие настройки на UI-потоке.
            // Если запись не подтверждена, намеренно не закрываем приложение: пользователь не
            // должен выбирать между применением обновления и сохранностью плейлиста/настроек.
            if (_settings is null || !SettingsManager.Save(_settings))
            {
                _velopackDiagnostics?.Failed(new InvalidOperationException("Settings save before planned update restart was not confirmed."));
                SetDownloading(false);
                RefreshVelopackRuntimePresentation();
                ShowError(LocalizationService.Get(LocalizationKey.UpdateVelopackSaveBeforeRestartFailed));
                return;
            }

            // После успешного snapshot это плановый restart, а не аварийное завершение. Ставим
            // marker до запуска Update.exe, чтобы ProcessExit не пытался второй раз писать JSON
            // с фонового потока и не выдавал ложное cross-thread предупреждение.
            App? plannedRestartApp = Application.Current as App;
            plannedRestartApp?.MarkPlannedUpdateRestart();

            // При успехе Update.exe завершит этот процесс, применит уже проверенный package
            // и запустит Lumisense заново. Если сам запуск updater бросит исключение, UI
            // останется живым, а аварийное сохранение снова будет доступно.
            try
            {
                service.ApplyAndRestart(_result.VelopackUpdate);
            }
            catch
            {
                plannedRestartApp?.CancelPlannedUpdateRestart();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            if (_isVelopackPaused)
                SetVelopackPausedUi();
            else
            {
                _velopackDiagnostics?.Cancelled();
                SetDownloading(false);
                RefreshVelopackRuntimePresentation();
            }
        }
        catch (Exception ex)
        {
            _velopackDiagnostics?.Failed(ex);
            SetDownloading(false);
            RefreshVelopackRuntimePresentation();
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

        await DownloadAndLaunchLegacyAssetAsync(isMsi: false);
    }

    // EXE и MSI получают одинаковую проверку SHA-256 и один жизненный цикл. При выборе нового
    // источника CancellationToken останавливает старый поток; после его завершения сетевой слой
    // удаляет .part, и здесь начинается независимая полная загрузка с новым URL.
    private async Task DownloadAndLaunchLegacyAssetAsync(bool isMsi)
    {
        string? originalUrl = isMsi ? _result.MsiDownloadUrl : _result.DownloadUrl;
        string? expectedSha256 = isMsi ? _result.MsiSha256 : _result.InstallerSha256;
        if (string.IsNullOrWhiteSpace(originalUrl) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            SetDownloading(false);
            ShowError(isMsi
                ? LocalizationService.Get(LocalizationKey.UpdateMsiMigrationUnavailable)
                : LocalizationService.Get(LocalizationKey.UpdateFailureMissingInstallerChecksum));
            return;
        }

        SetDownloading(true);
        _pauseController?.Dispose();
        _pauseController = new DownloadPauseController();
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressInfo>(UpdateDownloadProgressUi);

        try
        {
            string source = _settings?.UpdateDownloadSource ?? "GitHub";
            string downloadUrl = UpdateChecker.ApplyDownloadSource(originalUrl, source);
            string installerPath = isMsi
                ? await UpdateChecker.DownloadMsiAsync(downloadUrl, expectedSha256, progress, _pauseController, _downloadCts.Token)
                : await UpdateChecker.DownloadInstallerAsync(downloadUrl, expectedSha256, progress, _pauseController, _downloadCts.Token);

            if (isMsi)
            {
                SetPreparingForMsiMigration();
                UpdateChecker.LaunchMsiAndExit(installerPath, expectedSha256);
            }
            else
            {
                // Перед запуском самого установщика тоже показываем «Подготовка…», чтобы полоса
                // прогресса не выглядела зависшей между 100% и передачей управления Windows.
                SetPreparing(isVelopack: false);
                UpdateChecker.LaunchInstallerAndExit(installerPath, expectedSha256);
            }
        }
        catch (OperationCanceledException) when (_restartLegacyDownloadFromNewSource && IsLoaded)
        {
            _restartLegacyDownloadFromNewSource = false;
            await DownloadAndLaunchLegacyAssetAsync(isMsi);
        }
        catch (OperationCanceledException)
        {
            SetDownloading(false);
        }
        catch (Exception ex)
        {
            SetDownloading(false);
            Logger.Warn($"Не удалось скачать {(isMsi ? "MSI для перехода" : "EXE-установщик")}: {ex.Message}");
            ShowError(isMsi
                ? $"{LocalizationService.Get(LocalizationKey.UpdateMsiMigrationDownloadFailed)}\n\n{ex.Message}"
                : $"Не удалось скачать установщик: {ex.Message}");
        }
    }

    // Состояние загрузки скрывает исходную кнопку установки и показывает отдельные явные
    // действия PauseDownloadButton и CancelDownloadButton. Закрытие окна отменяет запрос.
    private bool _isDownloading;

    private void SetDownloading(bool isDownloading)
    {
        _isDownloading = isDownloading;
        if (!isDownloading)
        {
            _pauseController?.Dispose();
            _pauseController = null;
            _isVelopackDownload = false;
            _isVelopackPaused = false;
            _restartLegacyDownloadFromNewSource = false;
        }

        if (!_isMsiMigrationOnly)
            InstallButton.Visibility = isDownloading ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = LocalizationService.Translate("Скачать и установить");
        InstallButton.Appearance = ControlAppearance.Primary;
        PauseDownloadButton.IsEnabled = isDownloading;
        PauseDownloadButton.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.Content = LocalizationService.Get(LocalizationKey.UpdateDownloadCancel);
        CancelDownloadButton.IsEnabled = isDownloading;
        CancelDownloadButton.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        bool canChangeLegacySource = isDownloading && !_isVelopackDownload && _settings is not null;
        ChangeDownloadSourceButton.Content = LocalizationService.FormatKey(
            LocalizationKey.UpdateDownloadChangeSource, GetSelectedDownloadSourceDisplayName());
        ChangeDownloadSourceButton.IsEnabled = canChangeLegacySource && !_restartLegacyDownloadFromNewSource;
        ChangeDownloadSourceButton.Visibility = canChangeLegacySource ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.IsEnabled = !isDownloading;
        LaterButton.Visibility = isDownloading ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.IsEnabled = !isDownloading;
        MoreButton.Visibility = !isDownloading && !string.IsNullOrEmpty(_result.ReleaseNotesUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;
        MigrateToMsiButton.IsEnabled = !isDownloading;
        DownloadProgressBar.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        // Неопределённый — пока не пришёл первый отчёт о прогрессе с известным общим размером
        // (см. UpdateDownloadProgressUi); пустая полоса на 0% в первые доли секунды скачивания
        // выглядела как зависание сильнее, чем честная "думающая" анимация.
        DownloadProgressBar.IsIndeterminate = isDownloading;
        DownloadProgressBar.Value = 0;
        PhaseText.Text = isDownloading
            ? (_result.DeliveryKind == UpdateDeliveryKind.Velopack
                ? LocalizationService.Get(LocalizationKey.UpdateVelopackDownloadNeutral)
                : LocalizationService.Translate("Скачивание…"))
            : "";
        PhaseText.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        RefreshDownloadControlLabels();
    }

    private void SetVelopackPausedUi()
    {
        _isDownloading = true;
        PauseDownloadButton.Visibility = Visibility.Visible;
        PauseDownloadButton.IsEnabled = true;
        CancelDownloadButton.Visibility = Visibility.Visible;
        CancelDownloadButton.IsEnabled = true;
        PhaseText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackDownloadPaused);
        PhaseText.Visibility = Visibility.Visible;
        RefreshDownloadControlLabels();
    }

    private void RefreshDownloadControlLabels()
    {
        if (!_isDownloading) return;

        bool paused = _isVelopackPaused || _pauseController?.IsPaused == true;
        PauseDownloadButton.Content = LocalizationService.Get(
            paused ? LocalizationKey.UpdateDownloadResume : LocalizationKey.UpdateDownloadPause);
        CancelDownloadButton.Content = LocalizationService.Get(LocalizationKey.UpdateDownloadCancel);
        ChangeDownloadSourceButton.Content = LocalizationService.FormatKey(
            LocalizationKey.UpdateDownloadChangeSource, GetSelectedDownloadSourceDisplayName());
    }

    private string GetSelectedDownloadSourceDisplayName() =>
        GetDownloadSourceDisplayName(_settings?.UpdateDownloadSource ?? "GitHub");

    private static string GetDownloadSourceDisplayName(string sourceKey)
    {
        foreach (var source in UpdateChecker.DownloadSources)
        {
            if (string.Equals(source.Key, sourceKey, StringComparison.Ordinal))
                return source.DisplayName;
        }

        return UpdateChecker.DownloadSources[0].DisplayName;
    }

    // UpdateManager сообщает нормализованный процент для full/delta-цепочки, но не обещает
    // размер байтов. Показываем честный процент вместо выдуманного размера delta-пакета.
    private void UpdateVelopackProgressUi(int percentage)
    {
        int normalized = Math.Clamp(percentage, 0, 100);
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = normalized / 100d;
        _velopackDiagnostics?.Progress(normalized);
        RefreshVelopackRuntimePresentation();
        PhaseText.Text = LocalizationService.FormatKey(
            LocalizationKey.UpdateVelopackDownloadProgress,
            normalized,
            _velopackDiagnostics?.ElapsedText ?? "0:00");
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
        CancelDownloadButton.IsEnabled = false;
        DownloadProgressBar.IsIndeterminate = true;
        PhaseText.Text = LocalizationService.Get(LocalizationKey.UpdateMsiMigrationLaunching);
        PhaseText.Visibility = Visibility.Visible;
    }

    private void SetPreparing(bool isVelopack)
    {
        CancelDownloadButton.IsEnabled = false;
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
