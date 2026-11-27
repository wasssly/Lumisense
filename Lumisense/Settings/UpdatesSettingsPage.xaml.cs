using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class UpdatesSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;

    // Глушит Checked-обработчики источника, пока значение читается из настроек.
    private bool _isInitializing = true;

    private CancellationTokenSource? _sourceProbeCts;
    private IReadOnlyList<UpdateSourceProbeResult>? _sourceProbeResults;
    private CancellationTokenSource? _basePackageCts;
    private VelopackBasePackagePlan? _basePackagePlan;
    private bool _isVelopackManagedInstall;
    private string? _basePackageError;
    private string? _lastCheckedReleaseAssetUrl;
    private bool _isLegacyInnoCleanupRunning;
    private string? _legacyInnoCleanupStatusKey;

    internal UpdatesSettingsPage(Window window, AppSettings settings)
    {
        _window = window;
        _settings = settings;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        // Velopack берёт пакеты из своего feed на GitHub и зеркала не использует: выбор источника там ни на что не влиял бы.
        if (UpdateMigrationGuard.IsVelopackManagedInstall())
        {
            UpdateSourceContentPanel.IsEnabled = false;
            UpdateSourceVelopackHint.Text = LocalizationService.Translate("В этой установке обновления загружаются напрямую с GitHub; зеркала действуют только для установщика EXE/MSI.");
            UpdateSourceVelopackHint.Visibility = Visibility.Visible;
        }

        (_settings.UpdateDownloadSource switch
        {
            "GhProxy" => UpdateSourceGhProxyRadio,
            "GhProxyV4" => UpdateSourceGhProxyV4Radio,
            "GhProxyV6" => UpdateSourceGhProxyV6Radio,
            "GhProxyCdn" => UpdateSourceGhProxyCdnRadio,
            "GhProxyCom" => UpdateSourceGhProxyComRadio,
            "GhFast" => UpdateSourceGhFastRadio,
            _ => UpdateSourceGitHubRadio
        }).IsChecked = true;

        AllVersionsLoadingIndicator.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Loading);
        RefreshUpdateSourceProbePresentation();
        RefreshVelopackBasePackagePresentation();
        RefreshLegacyInnoCleanupPresentation();
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageUpdates, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    // Вызывается при каждом показе вкладки: план базового пакета читается с диска, а список версий грузится из сети только тут,
    // а не при открытии окна.
    public void OnShown()
    {
        if (_window.IsLoaded && _basePackagePlan is null)
            BackgroundTask.FireAndForget(LoadBasePackagePlanAsync(), nameof(LoadBasePackagePlanAsync));
        StartAllVersionsLoadIfNeeded();
    }

    public async Task LoadBasePackagePlanAsync()
    {
        await RefreshVelopackBasePackagePlanAsync();
        RefreshLegacyInnoCleanupPresentation();
    }

    public void RefreshLocalization()
    {
        if (_loadedReleases is not null)
            RenderAllVersions(_loadedReleases);

        RefreshUpdateSourceProbePresentation();
        RefreshVelopackBasePackagePresentation();
        RefreshLegacyInnoCleanupPresentation();
        if (_sourceProbeResults is not null)
            RenderUpdateSourceProbeResults(_sourceProbeResults);
    }

    public void CancelBackgroundWork()
    {
        _sourceProbeCts?.Cancel();
        _basePackageCts?.Cancel();
    }

    private async Task RefreshVelopackBasePackagePlanAsync()
    {
        _basePackageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _basePackageCts = cts;
        _basePackageError = null;

        try
        {
            // Создание UpdateManager и чтение локальных пакетов — файловый ввод-вывод: на UI-потоке он давал паузу сразу после открытия окна.
            (VelopackUpdateService service, bool isManaged) = await Task.Run(() =>
            {
                var created = new VelopackUpdateService();
                return (created, created.IsManagedInstall);
            }, cts.Token);
            _isVelopackManagedInstall = isManaged;
            if (!_isVelopackManagedInstall)
            {
                _basePackagePlan = VelopackBasePackagePlan.Unavailable(VelopackBasePackageStatus.NotManagedInstall);
                RefreshVelopackBasePackagePresentation();
                return;
            }

            _basePackagePlan = await Task.Run(() => service.GetBasePackagePlanAsync(cts.Token), cts.Token);
            RefreshVelopackBasePackagePresentation();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Окно закрыто, пользователь начал загрузку или запрос был заменён более свежим.
        }
        catch (Exception ex)
        {
            _basePackageError = LocalizationService.Get(LocalizationKey.UpdateFailureNetwork);
            RefreshVelopackBasePackagePresentation();
            Logger.Warn($"Не удалось проверить доступность Velopack base package: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_basePackageCts, cts))
                _basePackageCts = null;
            cts.Dispose();
        }
    }

    private void SetPrepareVelopackBasePackageButtonAvailability(bool isEnabled)
    {
        PrepareVelopackBasePackageButton.IsEnabled = isEnabled;
        PrepareVelopackBasePackageButton.Opacity = isEnabled ? 1.0 : 0.48;
    }

    private void RefreshVelopackBasePackagePresentation()
    {
        VelopackBasePackageTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageTitle);
        VelopackBasePackageDescriptionText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageDescription);
        PrepareVelopackBasePackageButtonText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageDownload);

        if (!_isVelopackManagedInstall || _basePackagePlan?.Status == VelopackBasePackageStatus.NotManagedInstall)
        {
            VelopackBasePackageExpander.Visibility = Visibility.Collapsed;
            return;
        }

        VelopackBasePackageExpander.Visibility = Visibility.Visible;
        if (_basePackagePlan is null)
        {
            SetPrepareVelopackBasePackageButtonAvailability(false);
            VelopackBasePackageStatusText.Text = string.IsNullOrWhiteSpace(_basePackageError)
                ? LocalizationService.Get(LocalizationKey.UpdateVelopackPreparing)
                : LocalizationService.FormatKey(LocalizationKey.UpdateVelopackBasePackageDownloadFailed, _basePackageError);
            return;
        }

        VelopackBasePackagePlan plan = _basePackagePlan;
        string version = plan.CurrentVersion?.ToString() ?? "—";
        switch (plan.Status)
        {
            case VelopackBasePackageStatus.Available when plan.FullPackage is not null:
                SetPrepareVelopackBasePackageButtonAvailability(true);
                VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                    LocalizationKey.UpdateVelopackBasePackageAvailable,
                    version,
                    VelopackUpdateDiagnostics.FormatBytes(plan.FullPackage.Size),
                    VelopackUpdateDiagnostics.FormatBytes(plan.RequiredFreeBytes));
                break;

            case VelopackBasePackageStatus.Prepared:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                PrepareVelopackBasePackageButtonText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageReady);
                VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                    LocalizationKey.UpdateVelopackBasePackagePrepared, version);
                break;

            case VelopackBasePackageStatus.CurrentPackageUnavailable:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                VelopackBasePackageStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageUnavailable);
                break;

            case VelopackBasePackageStatus.InsufficientDiskSpace:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                    LocalizationKey.UpdateVelopackBasePackageInsufficientSpace,
                    VelopackUpdateDiagnostics.FormatBytes(plan.RequiredFreeBytes));
                break;

            default:
                SetPrepareVelopackBasePackageButtonAvailability(false);
                VelopackBasePackageStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateVelopackBasePackageNotManaged);
                break;
        }
    }

    private void RefreshLegacyInnoCleanupPresentation()
    {
        LegacyInnoCleanupTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardTitle);
        LegacyInnoCleanupDescriptionText.Text = LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardDescription);
        RemoveLegacyInnoButtonText.Text = LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardButton);

        bool hasLegacyInstall = _isVelopackManagedInstall &&
                                LegacyInnoCleanupService.TryFind(out LegacyInnoCleanupService.LegacyInnoInstall? legacyInstall) &&
                                legacyInstall is not null;
        if (!hasLegacyInstall)
        {
            LegacyInnoCleanupExpander.Visibility = Visibility.Collapsed;
            return;
        }

        LegacyInnoCleanupExpander.Visibility = Visibility.Visible;
        RemoveLegacyInnoButton.IsEnabled = !_isLegacyInnoCleanupRunning;
        string statusKey = _isLegacyInnoCleanupRunning
            ? LocalizationKey.UpdateLegacyCleanupCardRunning
            : _legacyInnoCleanupStatusKey ?? LocalizationKey.UpdateLegacyCleanupCardAvailable;
        LegacyInnoCleanupStatusText.Text = LocalizationService.Get(statusKey);
    }

    private void RemoveLegacyInnoButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(RemoveLegacyInnoButton_ClickAsync(sender, e), nameof(RemoveLegacyInnoButton_Click));

    private async Task RemoveLegacyInnoButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (!_isVelopackManagedInstall || _isLegacyInnoCleanupRunning ||
            !LegacyInnoCleanupService.TryFind(out LegacyInnoCleanupService.LegacyInnoInstall? legacyInstall) ||
            legacyInstall is null)
        {
            RefreshLegacyInnoCleanupPresentation();
            return;
        }

        var answer = LocalizedMessageBox.Show(
            _window,
            LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupMessage),
            LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        if (!LegacyInnoCleanupService.TryStartInteractiveUninstall(
                legacyInstall,
                out System.Diagnostics.Process? uninstallerProcess,
                out string? technicalError) ||
            uninstallerProcess is null)
        {
            _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupFailed;
            RefreshLegacyInnoCleanupPresentation();
            LocalizedMessageBox.Show(
                _window,
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupFailed),
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            if (!string.IsNullOrWhiteSpace(technicalError))
                Logger.Warn($"Legacy cleanup не был запущен из Settings: {technicalError}");
            return;
        }

        _isLegacyInnoCleanupRunning = true;
        _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardRunning;
        RefreshLegacyInnoCleanupPresentation();

        try
        {
            await uninstallerProcess.WaitForExitAsync();
            bool stillInstalled = LegacyInnoCleanupService.TryFind(out _);
            if (stillInstalled)
            {
                _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardStillInstalled;
                Logger.Info("Legacy EXE-копия Lumisense осталась после закрытия мастера удаления.");
                RefreshLegacyInnoCleanupPresentation();
                return;
            }

            string? legacyInstallDir = Path.GetDirectoryName(legacyInstall.UninstallerPath);
            if (!string.IsNullOrWhiteSpace(legacyInstallDir))
                LegacyIntegrationRepairService.RepairAfterLegacyCleanup(legacyInstallDir);

            _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardCompleted;
            RefreshLegacyInnoCleanupPresentation();
            if (_window.IsLoaded)
            {
                LocalizedMessageBox.Show(
                    _window,
                    LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupCardCompleted),
                    LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _legacyInnoCleanupStatusKey = LocalizationKey.UpdateLegacyCleanupCardStillInstalled;
            Logger.Warn($"Не удалось дождаться завершения legacy cleanup: {ex.Message}");
            RefreshLegacyInnoCleanupPresentation();
        }
        finally
        {
            _isLegacyInnoCleanupRunning = false;
            RefreshLegacyInnoCleanupPresentation();
            uninstallerProcess.Dispose();
        }
    }

    private void PrepareVelopackBasePackageButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(PrepareVelopackBasePackageButton_ClickAsync(sender, e), nameof(PrepareVelopackBasePackageButton_Click));

    private async Task PrepareVelopackBasePackageButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (_basePackagePlan?.Status != VelopackBasePackageStatus.Available)
            return;

        _basePackageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _basePackageCts = cts;
        SetPrepareVelopackBasePackageButtonAvailability(false);
        var progress = new Progress<int>(value =>
        {
            VelopackBasePackageStatusText.Text = LocalizationService.FormatKey(
                LocalizationKey.UpdateVelopackBasePackageDownloading, value);
        });

        try
        {
            var service = new VelopackUpdateService();
            await service.PrepareBasePackageAsync(_basePackagePlan, progress, cts.Token);
            _basePackagePlan = null;
            await RefreshVelopackBasePackagePlanAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _basePackagePlan = null;
            await RefreshVelopackBasePackagePlanAsync();
        }
        catch (Exception ex)
        {
            _basePackageError = LocalizationService.Get(LocalizationKey.UpdateFailureNetwork);
            RefreshVelopackBasePackagePresentation();
            Logger.Warn($"Не удалось подготовить Velopack base package: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_basePackageCts, cts))
                _basePackageCts = null;
            cts.Dispose();
        }
    }

    // Ручная проверка (кнопка на странице "О плеере"), в отличие от тихой на старте (MainWindow.CheckForUpdatesOnStartupAsync),
    // всегда показывает результат и не учитывает SkippedUpdateVersion: пользователь явно хочет знать статус.
    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(CheckUpdatesButton_ClickAsync(sender, e), nameof(CheckUpdatesButton_Click));

    private async Task CheckUpdatesButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButtonSubtitle.Text = LocalizationService.Translate("Проверяем…");
        SetUpdateCheckBusy(true);

        try
        {
            var result = await UpdateChecker.CheckAsync();
            SetUpdateCheckBusy(false);
            if (result.Status != UpdateCheckStatus.Error)
            {
                // Только legacy Inno Setup проверяет EXE-asset. Velopack сам определяет
                // latest full package через release API в момент диагностики.
                _lastCheckedReleaseAssetUrl = result.DeliveryKind == UpdateDeliveryKind.Velopack
                    ? null
                    : result.DownloadUrl;
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    CheckUpdatesButtonSubtitle.Text = LocalizationService.Translate($"Доступна версия {result.LatestVersion}");
                    new UpdateAvailableWindow(result, _settings) { Owner = _window }.ShowDialog();
                    break;

                case UpdateCheckStatus.MsiMigrationAvailable:
                    CheckUpdatesButtonSubtitle.Text = LocalizationService.Get(
                        LocalizationKey.UpdateMsiMigrationManualSubtitle);
                    new UpdateAvailableWindow(result, _settings) { Owner = _window }.ShowDialog();
                    break;

                case UpdateCheckStatus.UpToDate:
                    CheckUpdatesButtonSubtitle.Text = LocalizationService.Translate($"У вас последняя версия ({result.CurrentVersion})");
                    break;

                case UpdateCheckStatus.Error:
                default:
                    // Если GitHub недоступен, подсказываем запасной путь: релизы дублируются в Telegram-канале.
                    CheckUpdatesButtonSubtitle.Text = UpdateFailureExperience.Describe(result.FailureKind, result.HttpStatusCode)
                        + " " + string.Format(
                            LocalizationService.Translate("Новые версии также публикуются в Telegram: {0}"),
                            UpdateCheckHealth.TelegramChannelName);
                    break;
            }
        }
        finally
        {
            SetUpdateCheckBusy(false);
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    // Пока идёт проверка, вместо галочки в карточке крутится индикатор Material.
    private void SetUpdateCheckBusy(bool busy)
    {
        CheckUpdatesLoadingIndicator.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Loading);
        CheckUpdatesLoadingIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    // Источник применяется сразу: UpdateAvailableWindow использует ту же in-memory модель _settings; выбор сохраняем
        // сразу, чтобы он пережил перезапуск без ожидания общего сохранения.
    private void UpdateSourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key } radio) return;
        if (key != "GitHub" && _settings.UpdateDownloadSource == "GitHub")
        {
            // Диалог внутри Checked оставлял обе радиокнопки отмеченными, поэтому показываем его после обработчика.
            Dispatcher.BeginInvoke(() => ConfirmAndApplyMirror(radio, key));
            return;
        }
        ApplyUpdateSource(key);
    }

    private void ConfirmAndApplyMirror(System.Windows.Controls.RadioButton mirrorRadio, string key)
    {
        if (ConfirmMirrorUse())
        {
            ApplyUpdateSource(key);
            return;
        }

        // Откат без повторного срабатывания Checked и без записи выбора в настройки.
        bool wasInitializing4 = _isInitializing;
        _isInitializing = true;
        try
        {
            mirrorRadio.IsChecked = false;
            UpdateSourceGitHubRadio.IsChecked = true;
        }
        finally
        {
            _isInitializing = wasInitializing4;
        }
    }

    private void ApplyUpdateSource(string key)
    {
        _settings.UpdateDownloadSource = key;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Предупреждаем только при переходе с GitHub на зеркало; переключение между зеркалами уже подтверждено.
    private bool ConfirmMirrorUse()
    {
        if (_settings.UpdateMirrorWarningSuppressed) return true;

        var dialog = new UpdateMirrorWarningDialog(_window);
        if (dialog.ShowDialog() != true) return false;

        _settings.UpdateMirrorWarningSuppressed = dialog.DontAskAgain;
        return true;
    }

    private void ProbeUpdateSourcesButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ProbeUpdateSourcesButton_ClickAsync(sender, e), nameof(ProbeUpdateSourcesButton_Click));

    private async Task ProbeUpdateSourcesButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (!ProbeUpdateSourcesButton.IsEnabled) return;

        ProbeUpdateSourcesButton.IsEnabled = false;
        ClearUpdateSourceProbeRows();
        _sourceProbeCts?.Cancel();
        _sourceProbeCts = new CancellationTokenSource();
        UpdateSourceProbeStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeRunning);
        UpdateSourceProbeStatusText.Visibility = Visibility.Visible;

        try
        {
            _sourceProbeResults = await UpdateChecker.ProbeDownloadSourcesAsync(
                _lastCheckedReleaseAssetUrl, _sourceProbeCts.Token);
            RenderUpdateSourceProbeResults(_sourceProbeResults);
        }
        catch (OperationCanceledException)
        {
            // Закрытие окна или новая проверка отменили предыдущий ограниченный test-запрос.
        }
        catch (UpdateSourceProbeAssetResolutionException ex)
        {
            Logger.Warn($"Не удалось определить актуальный release asset для диагностики: {ex.Message}");
            UpdateSourceProbeStatusText.Text = LocalizationService.Get(
                LocalizationKey.UpdateSourceProbeAssetUnavailable);
            UpdateSourceProbeStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось выполнить диагностику источников обновлений: {ex.Message}");
            UpdateSourceProbeStatusText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeNoWorking);
            UpdateSourceProbeStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (_window.IsLoaded)
                ProbeUpdateSourcesButton.IsEnabled = true;
        }
    }

    private void RefreshUpdateSourceProbePresentation()
    {
        ProbeUpdateSourcesTitleText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeTitle);
        ProbeUpdateSourcesSubtitleText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeSubtitle);
    }

    private void RenderUpdateSourceProbeResults(IReadOnlyList<UpdateSourceProbeResult> results)
    {
        foreach (UpdateSourceProbeResult result in results)
        {
            System.Windows.Controls.TextBlock? statusText = GetUpdateSourceProbeText(result.Key);
            if (statusText is null) continue;

            bool isSlow = result.IsAvailable &&
                          (result.BytesPerSecond < 128 * 1024 || result.ResponseMilliseconds > 1500);
            if (!result.IsAvailable)
            {
                statusText.Text = LocalizationService.Get(LocalizationKey.UpdateSourceProbeFailed);
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(224, 82, 82));
            }
            else if (isSlow)
            {
                statusText.Text = LocalizationService.FormatKey(LocalizationKey.UpdateSourceProbeRow,
                    LocalizationService.Get(LocalizationKey.UpdateSourceProbeSlow),
                    result.ResponseMilliseconds, FormatProbeRate(result.BytesPerSecond));
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(216, 158, 34));
            }
            else
            {
                statusText.Text = LocalizationService.FormatKey(LocalizationKey.UpdateSourceProbeRow,
                    LocalizationService.Get(LocalizationKey.UpdateSourceProbeGood),
                    result.ResponseMilliseconds, FormatProbeRate(result.BytesPerSecond));
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(63, 174, 106));
            }

            statusText.Visibility = Visibility.Visible;
        }

        UpdateSourceProbeResult? recommended = results
            .Where(result => result.IsAvailable)
            .OrderByDescending(result => result.BytesPerSecond)
            .ThenBy(result => result.ResponseMilliseconds)
            .FirstOrDefault();
        UpdateSourceProbeStatusText.Text = recommended is null
            ? LocalizationService.Get(LocalizationKey.UpdateSourceProbeNoWorking)
            : LocalizationService.FormatKey(LocalizationKey.UpdateSourceProbeRecommended,
                LocalizationService.Translate(recommended.DisplayName));
        UpdateSourceProbeStatusText.Visibility = Visibility.Visible;
    }

    private System.Windows.Controls.TextBlock? GetUpdateSourceProbeText(string sourceKey) => sourceKey switch
    {
        "GitHub" => UpdateSourceGitHubProbeText,
        "GhProxy" => UpdateSourceGhProxyProbeText,
        "GhProxyV4" => UpdateSourceGhProxyV4ProbeText,
        "GhProxyV6" => UpdateSourceGhProxyV6ProbeText,
        "GhProxyCdn" => UpdateSourceGhProxyCdnProbeText,
        "GhProxyCom" => UpdateSourceGhProxyComProbeText,
        "GhFast" => UpdateSourceGhFastProbeText,
        _ => null
    };

    private void ClearUpdateSourceProbeRows()
    {
        foreach (string sourceKey in UpdateChecker.DownloadSources.Select(source => source.Key))
        {
            System.Windows.Controls.TextBlock? statusText = GetUpdateSourceProbeText(sourceKey);
            if (statusText is null) continue;
            statusText.Text = string.Empty;
            statusText.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatProbeRate(double bytesPerSecond)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        string megabytes = LocalizationService.IsEnglish ? "MB" : "МБ";
        string kilobytes = LocalizationService.IsEnglish ? "KB" : "КБ";
        return bytesPerSecond >= mib
            ? $"{bytesPerSecond / mib:F1} {megabytes}"
            : bytesPerSecond >= kib
                ? $"{bytesPerSecond / kib:F0} {kilobytes}"
                : $"{bytesPerSecond:F0} B";
    }


    // Элемент списка версий (AllVersionsList в XAML): обёртка над ReleaseListItem с готовыми строками, чтобы DataTemplate
    // был набором биндингов без конвертеров.
    private sealed record VersionListItemViewModel(
        string TitleText, string SubtitleText, string ActionText, bool CanInstall, ReleaseListItem Release);

    private bool _allVersionsLoaded;
    private bool _allVersionsLoading;
    private IReadOnlyList<ReleaseListItem>? _loadedReleases;

    // Список грузится лениво — при первом раскрытии аккордеона и один раз за жизнь окна, чтобы не бить по сети зря.
    private void AllVersionsExpander_Expanded(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(AllVersionsExpander_ExpandedAsync(sender, e), nameof(AllVersionsExpander_Expanded));

    private async Task AllVersionsExpander_ExpandedAsync(object sender, RoutedEventArgs e)
    {
        // Expanded срабатывает ещё при разборе XAML, когда элементы ниже не созданы: тогда ничего не грузим,
        // загрузка стартует при показе вкладки (раньше NullReferenceException оставлял _allVersionsLoading взведённым).
        if (AllVersionsLoadingText is null) return;

        if (_allVersionsLoaded || _allVersionsLoading) return;
        await LoadAllVersionsAsync();
    }

    // Загружает список при показе вкладки «Обновления», если аккордеон уже раскрыт (раскрыт по умолчанию), а список ещё не получен.
    private void StartAllVersionsLoadIfNeeded()
    {
        if (!AllVersionsExpanderControl.IsExpanded || _allVersionsLoaded || _allVersionsLoading) return;
        BackgroundTask.FireAndForget(LoadAllVersionsAsync(), nameof(StartAllVersionsLoadIfNeeded));
    }

    private void AllVersionsRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allVersionsLoading) return;
        BackgroundTask.FireAndForget(LoadAllVersionsAsync(), nameof(AllVersionsRetryButton_Click));
    }

    // Загружает список версий; неудача не помечает список загруженным, поэтому «Повторить» и повторное раскрытие пробуют снова.
    // Сбой GitHub (лимит 403/429, сеть) обрабатывает UpdateChecker.GetAllReleasesAsync: повторы и сохранённая копия списка.
    private async Task LoadAllVersionsAsync()
    {
        _allVersionsLoading = true;
        try
        {
            AllVersionsLoadingText.Visibility = Visibility.Visible;
            AllVersionsErrorText.Visibility = Visibility.Collapsed;
            AllVersionsStaleText.Visibility = Visibility.Collapsed;
            AllVersionsRetryButton.Visibility = Visibility.Collapsed;
            AllVersionsRetryButton.IsEnabled = false;

            // Сначала сразу показываем сохранённый список (если есть), а свежий подгружаем следом: раньше при медленном GitHub
            // или VPN окно до конца запроса показывало только неменяющуюся надпись о загрузке.
            if (_loadedReleases is null && UpdateChecker.GetCachedReleases() is { Releases.Count: > 0 } cached)
            {
                _loadedReleases = cached.Releases;
                RenderAllVersions(cached.Releases);
                AllVersionsLoadingText.Text = LocalizationService.Translate("Обновляем список версий…");
            }

            var attemptProgress = new Progress<int>(attempt =>
            {
                if (_loadedReleases is null)
                    AllVersionsLoadingText.Text = string.Format(
                        LocalizationService.Translate("Загружаем список версий… попытка {0} из {1}"),
                        attempt, ReleaseListRetryPolicy.MaxAttempts);
            });
            ReleaseListResult releaseResult = await UpdateChecker.GetAllReleasesAsync(attemptProgress: attemptProgress);
            AllVersionsLoadingText.Visibility = Visibility.Collapsed;

            if (!releaseResult.IsSuccess)
            {
                AllVersionsErrorText.Text = DescribeVersionListFailure(releaseResult.FailureKind, releaseResult.HttpStatusCode,
                    releaseResult.RateLimitResetAt, releaseResult);
                AllVersionsErrorText.Visibility = Visibility.Visible;
                AllVersionsRetryButton.Visibility = Visibility.Visible;
                return;
            }

            IReadOnlyList<ReleaseListItem> releases = releaseResult.Releases;
            if (releases.Count == 0)
            {
                _allVersionsLoaded = true;
                AllVersionsErrorText.Text = LocalizationService.Translate("На GitHub пока нет ни одного опубликованного релиза.");
                AllVersionsErrorText.Visibility = Visibility.Visible;
                return;
            }

            _allVersionsLoaded = !releaseResult.IsFromCache;
            _loadedReleases = releases;
            RenderAllVersions(releases);

            if (releaseResult.IsFromCache)
            {
                string reason = DescribeVersionListFailure(releaseResult.RefreshFailureKind, releaseResult.RefreshHttpStatusCode,
                    releaseResult.RateLimitResetAt, null);
                string saved = releaseResult.CachedAt is { } cachedAt
                    ? string.Format(LocalizationService.Translate("Показан сохранённый список от {0}."), cachedAt.ToLocalTime().ToString("g"))
                    : LocalizationService.Translate("Показан сохранённый список.");
                AllVersionsStaleText.Text = $"{saved} {reason}";
                AllVersionsStaleText.Visibility = Visibility.Visible;
                AllVersionsRetryButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _allVersionsLoading = false;
            AllVersionsRetryButton.IsEnabled = true;
        }
    }

    // Лимит запросов GitHub объясняем словами; прочие сбои — стандартным текстом.
    private static string DescribeVersionListFailure(UpdateFailureKind kind, int? httpStatusCode, DateTimeOffset? resetAt,
        ReleaseListResult? fullResult)
    {
        if (UpdateFailureExperience.IsRateLimit(kind, httpStatusCode))
            return UpdateFailureExperience.DescribeRateLimit(httpStatusCode!.Value, resetAt);

        return fullResult is not null
            ? UpdateFailureExperience.DescribeVersionListFailure(fullResult)
            : UpdateFailureExperience.Describe(kind, httpStatusCode);
    }

    private void RenderAllVersions(IReadOnlyList<ReleaseListItem> releases)
    {
        string currentVersion = UpdateChecker.GetCurrentVersion();
        bool velopackInstall = UpdateMigrationGuard.IsVelopackManagedInstall();

        AllVersionsList.ItemsSource = releases
            // GitHub отдаёт релизы уже в порядке "сначала новые", но не гарантирует это явно —
            // сортируем сами по дате публикации, чтобы порядок не зависел от их API.
            .OrderByDescending(r => r.PublishedAt ?? System.DateTimeOffset.MinValue)
            .Select(r =>
            {
                bool isCurrent = string.Equals(r.Version, currentVersion, System.StringComparison.OrdinalIgnoreCase);

                string title = $"v{r.Version}" +
                    (isCurrent ? $" · {LocalizationService.Translate("Текущая версия")}" : "") +
                    (r.IsPrerelease ? $" · {LocalizationService.Translate("Пререлиз")}" : "");
                string subtitle = r.PublishedAt is { } published
                    ? published.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo(
                        LocalizationService.IsEnglish ? "en-US" : "ru-RU"))
                    : LocalizationService.Translate("Дата публикации неизвестна");

                // Inno EXE поверх Velopack-установки создал бы вторую копию, поэтому там кнопка только открывает релиз.
                bool canInstall = velopackInstall
                    ? !string.IsNullOrEmpty(r.ReleaseNotesUrl)
                    : !string.IsNullOrEmpty(r.ExeDownloadUrl) && !string.IsNullOrEmpty(r.ExeSha256);
                if (!velopackInstall && string.IsNullOrEmpty(r.ExeDownloadUrl))
                    subtitle += $" · {LocalizationService.Translate("В релизе нет .exe-установщика")}";
                else if (!velopackInstall && string.IsNullOrEmpty(r.ExeSha256))
                    subtitle += $" · {LocalizationService.Translate("В релизе отсутствует SHA-256 установщика")}";

                string action = LocalizationService.Translate(velopackInstall ? "Открыть релиз"
                    : isCurrent ? "Переустановить" : "Установить");

                return new VersionListItemViewModel(title, subtitle, action, canInstall, r);
            })
            .ToList();
    }

    // Тот же диалог, что при обычном обнаружении обновления; не проверяет, что версия новее, поэтому подходит и для отката:
    // CurrentVersion — реальная текущая, диалог показывает обе рядом (это и есть предупреждение об откате).
    private void VersionListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionListItemViewModel item }) return;

        if (UpdateMigrationGuard.IsVelopackManagedInstall())
        {
            if (!string.IsNullOrEmpty(item.Release.ReleaseNotesUrl)) SettingsWindow.OpenUrl(item.Release.ReleaseNotesUrl);
            return;
        }

        var result = new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            CurrentVersion = UpdateChecker.GetCurrentVersion(),
            LatestVersion = item.Release.Version,
            DownloadUrl = item.Release.ExeDownloadUrl,
            InstallerSha256 = item.Release.ExeSha256,
            ReleaseNotesUrl = item.Release.ReleaseNotesUrl,
            ReleaseNotes = item.Release.ReleaseNotes,
        };

        try
        {
            new UpdateAvailableWindow(result, _settings) { Owner = _window }.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Error($"Не удалось открыть окно информации об обновлении: {ex}");
            LocalizedMessageBox.Show(_window,
                "Не удалось открыть информацию об обновлении. Подробности записаны в журнал.",
                "Ошибка обновления", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
