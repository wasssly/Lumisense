using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace AudioPlayer;

/// <summary>
/// Транспорт обновлений только для прототипа Velopack.
///
/// Он намеренно не подключён к текущему UpdateAvailableWindow: пользователи существующей
/// Inno Setup-установки не имеют Velopack package store и не могут безопасно применить delta.
/// После проверки переходного релиза UI будет переключаться на этот сервис только тогда,
/// когда <see cref="IsManagedInstall"/> возвращает true.
/// </summary>
internal sealed class VelopackUpdateService
{
    internal const string PackId = "Wasssly.Lumisense";
    internal const string ReleaseChannel = "win";
    private const string RepositoryUrl = "https://github.com/wasssly/Lumisense";

#if VELOPACK_LOCAL_FEED_TEST
    // Этот override существует только в ручном test build. В обычном release код ниже
    // не компилируется и переменная среды не может перенаправить пользователей с GitHub.
    internal const string LocalTestFeedEnvironmentVariable = "LUMISENSE_VELOPACK_TEST_FEED";
#endif

    private const long BasePackageSafetyMarginBytes = 128L * 1024 * 1024;

    private readonly UpdateManager _manager;
    private readonly IUpdateSource _source;

    public VelopackUpdateService()
    {
        _manager = CreateManager(out bool usesLocalTestFeed, out IUpdateSource source);
        _source = source;
        UsesLocalTestFeed = usesLocalTestFeed;
    }

    public bool IsManagedInstall => _manager.IsInstalled;
    public bool UsesLocalTestFeed { get; }

    private static UpdateManager CreateManager(out bool usesLocalTestFeed, out IUpdateSource source)
    {
        var options = new UpdateOptions { ExplicitChannel = ReleaseChannel };

#if VELOPACK_LOCAL_FEED_TEST
        string? localFeedPath = Environment.GetEnvironmentVariable(LocalTestFeedEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(localFeedPath))
        {
            usesLocalTestFeed = true;
            source = new SimpleFileSource(new DirectoryInfo(localFeedPath));
            return new UpdateManager(source, options);
        }
#endif

        usesLocalTestFeed = false;
        source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: false);
        return new UpdateManager(source, options);
    }

    /// <summary>
    /// Не обращается к GitHub для обычных Inno Setup, portable и debug-запусков.
    /// Это исключает ошибочный переход на новую систему до миграционного релиза.
    /// </summary>
    public async Task<VelopackProbeResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsManagedInstall)
            return VelopackProbeResult.LegacyInstall();

        try
        {
            UpdateInfo? update = await _manager.CheckForUpdatesAsync();
            return update is null
                ? VelopackProbeResult.UpToDate()
                : VelopackProbeResult.UpdateAvailable(update);
        }
        catch (Exception ex)
        {
            return VelopackProbeResult.Failed(ex.Message);
        }
    }

    public async Task DownloadAsync(
        UpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsManagedInstall)
            throw new InvalidOperationException("Delta-обновление доступно только для установки, созданной Velopack.");

        await _manager.DownloadUpdatesAsync(update, progress is null ? null : progress.Report, cancellationToken);
    }

    /// <summary>
    /// Возвращает состояние добровольной подготовки full package текущей установленной версии.
    /// Такой package нужен Velopack как локальная база для последующих delta-обновлений, но
    /// метод не ищет и не скачивает более новую версию приложения.
    /// </summary>
    public async Task<VelopackBasePackagePlan> GetBasePackagePlanAsync(CancellationToken cancellationToken = default)
    {
        Velopack.SemanticVersion? currentVersion = _manager.CurrentVersion;
        if (!IsManagedInstall || currentVersion is null || !VelopackLocator.IsCurrentSet)
            return VelopackBasePackagePlan.Unavailable(VelopackBasePackageStatus.NotManagedInstall);

        IVelopackLocator locator = VelopackLocator.Current;
        VelopackAsset? localPackage = VelopackBasePackagePlan.FindCurrentFullPackage(
            locator.GetLocalPackages(), currentVersion);
        if (localPackage is not null)
            return VelopackBasePackagePlan.Prepared(currentVersion, localPackage);

        VelopackAssetFeed feed = await _source.GetReleaseFeed(
            locator.Log,
            _manager.AppId,
            ReleaseChannel,
            locator.GetOrCreateStagedUserId(),
            latestLocalRelease: null);
        cancellationToken.ThrowIfCancellationRequested();

        VelopackAsset? remotePackage = VelopackBasePackagePlan.FindCurrentFullPackage(
            feed.Assets, currentVersion);
        if (remotePackage is null)
            return VelopackBasePackagePlan.Unavailable(VelopackBasePackageStatus.CurrentPackageUnavailable, currentVersion);

        long requiredBytes = checked(remotePackage.Size + BasePackageSafetyMarginBytes);
        if (!HasSufficientDiskSpace(locator.PackagesDir, requiredBytes))
            return VelopackBasePackagePlan.InsufficientSpace(currentVersion, remotePackage, requiredBytes);

        return VelopackBasePackagePlan.Available(currentVersion, remotePackage, requiredBytes);
    }

    /// <summary>
    /// Скачивает и проверяет full package текущей установленной версии в штатную папку Velopack.
    /// Обновление не применяется: TargetFullRelease совпадает с CurrentVersion, поэтому при
    /// следующем запуске не возникает pending newer update.
    /// </summary>
    public async Task PrepareBasePackageAsync(
        VelopackBasePackagePlan plan,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan.Status != VelopackBasePackageStatus.Available || plan.FullPackage is null)
            throw new InvalidOperationException("Текущий full package недоступен для подготовки локальной базы.");
        Velopack.SemanticVersion? currentVersion = _manager.CurrentVersion;
        if (!IsManagedInstall || currentVersion is null || !plan.CurrentVersion.Equals(currentVersion))
            throw new InvalidOperationException("Состояние установки изменилось; обновите информацию о подготовке базы.");

        Logger.Info($"Пользователь начал добровольную подготовку full package {plan.FullPackage.Version} для будущих delta-обновлений.");
        var currentPackage = new UpdateInfo(plan.FullPackage, isDowngrade: false);
        await _manager.DownloadUpdatesAsync(currentPackage, progress is null ? null : progress.Report, cancellationToken);
        Logger.Info($"Добровольная подготовка full package {plan.FullPackage.Version} завершена.");
    }

    public void ApplyAndRestart(UpdateInfo update)
    {
        if (!IsManagedInstall)
            throw new InvalidOperationException("Нельзя применить Velopack-обновление к старой Inno Setup-установке.");

        _manager.ApplyUpdatesAndRestart(update);
    }

    private static bool HasSufficientDiskSpace(string? packagesDirectory, long requiredBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(packagesDirectory)) return false;
            string? root = Path.GetPathRoot(packagesDirectory);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).AvailableFreeSpace >= requiredBytes;
        }
        catch
        {
            return false;
        }
    }
}

internal enum VelopackProbeStatus
{
    LegacyInstall,
    UpToDate,
    UpdateAvailable,
    Error,
}

internal sealed class VelopackProbeResult
{
    private VelopackProbeResult(VelopackProbeStatus status, UpdateInfo? update = null, string? technicalDetail = null)
    {
        Status = status;
        Update = update;
        TechnicalDetail = technicalDetail;
    }

    public VelopackProbeStatus Status { get; }
    public UpdateInfo? Update { get; }
    public string? TechnicalDetail { get; }

    public static VelopackProbeResult LegacyInstall() => new(VelopackProbeStatus.LegacyInstall);
    public static VelopackProbeResult UpToDate() => new(VelopackProbeStatus.UpToDate);
    public static VelopackProbeResult UpdateAvailable(UpdateInfo update) => new(VelopackProbeStatus.UpdateAvailable, update);
    public static VelopackProbeResult Failed(string technicalDetail) => new(VelopackProbeStatus.Error, technicalDetail: technicalDetail);
}

internal enum VelopackBasePackageStatus
{
    NotManagedInstall,
    Available,
    Prepared,
    CurrentPackageUnavailable,
    InsufficientDiskSpace,
}

internal sealed class VelopackBasePackagePlan
{
    private VelopackBasePackagePlan(
        VelopackBasePackageStatus status,
        Velopack.SemanticVersion? currentVersion = null,
        VelopackAsset? fullPackage = null,
        long requiredFreeBytes = 0)
    {
        Status = status;
        CurrentVersion = currentVersion;
        FullPackage = fullPackage;
        RequiredFreeBytes = requiredFreeBytes;
    }

    public VelopackBasePackageStatus Status { get; }
    public Velopack.SemanticVersion? CurrentVersion { get; }
    public VelopackAsset? FullPackage { get; }
    public long RequiredFreeBytes { get; }

    public static VelopackBasePackagePlan Available(Velopack.SemanticVersion currentVersion, VelopackAsset fullPackage, long requiredFreeBytes)
        => new(VelopackBasePackageStatus.Available, currentVersion, fullPackage, requiredFreeBytes);

    public static VelopackBasePackagePlan Prepared(Velopack.SemanticVersion currentVersion, VelopackAsset fullPackage)
        => new(VelopackBasePackageStatus.Prepared, currentVersion, fullPackage);

    public static VelopackBasePackagePlan Unavailable(VelopackBasePackageStatus status, Velopack.SemanticVersion? currentVersion = null)
        => new(status, currentVersion);

    public static VelopackBasePackagePlan InsufficientSpace(Velopack.SemanticVersion currentVersion, VelopackAsset fullPackage, long requiredFreeBytes)
        => new(VelopackBasePackageStatus.InsufficientDiskSpace, currentVersion, fullPackage, requiredFreeBytes);

    internal static VelopackAsset? FindCurrentFullPackage(System.Collections.Generic.IEnumerable<VelopackAsset> assets, Velopack.SemanticVersion currentVersion)
        => assets.FirstOrDefault(asset => asset.Type == VelopackAssetType.Full && asset.Version.Equals(currentVersion));
}

/// <summary>
/// Центральная граница миграции: пока приложение запущено из старого Inno Setup, остаётся
/// действующей существующая проверка SHA-256 и Inno Setup. Delta включаются лишь после
/// осознанной установки Velopack MSI в переходном релизе.
/// </summary>
internal static class UpdateMigrationGuard
{
    public static bool IsVelopackManagedInstall()
    {
        try
        {
            return new VelopackUpdateService().IsManagedInstall;
        }
        catch
        {
            return false;
        }
    }

    public static void LogCurrentMode()
    {
        try
        {
            var service = new VelopackUpdateService();
            if (service.IsManagedInstall)
            {
                Logger.Info(service.UsesLocalTestFeed
                    ? "Режим обновлений: Velopack test build (локальный update feed)."
                    : "Режим обновлений: Velopack (доступны full/delta пакеты)."
                );
                return;
            }
        }
        catch
        {
            // Ниже безопасный legacy log, если UpdateManager недоступен вне установки.
        }

        Logger.Info("Режим обновлений: legacy Inno Setup (сохраняется проверенный SHA-256 установщик).");
    }

    /// <summary>
    /// Вызывается только после успешного создания главного окна при первом запуске MSI/Velopack.
    /// Сначала проверяется точный AppId legacy Inno Setup, затем пользователь сам решает, открыть
    /// ли штатный деинсталлятор старой EXE-копии. Данные %AppData%\Lumisense не удаляются кодом
    /// приложения и в мастере удаления пользователь получает явную инструкцию сохранить их.
    /// </summary>
    public static void TryShowFirstRunNotice()
    {
        if (!IsVelopackManagedInstall() || !VelopackMigrationLifecycle.TryConsumeFirstRunMarker())
            return;

        try
        {
            if (!LegacyInnoCleanupService.TryFind(out LegacyInnoCleanupService.LegacyInnoInstall? legacyInstall) ||
                legacyInstall is null)
            {
                LocalizedMessageBox.Show(
                    LocalizationService.Get(LocalizationKey.UpdateVelopackFirstRunMessage),
                    LocalizationService.Get(LocalizationKey.UpdateVelopackFirstRunTitle),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var answer = LocalizedMessageBox.Show(
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupMessage),
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information,
                System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                Logger.Info("Пользователь оставил legacy EXE-копию Lumisense после первого запуска MSI.");
                return;
            }

            // Must run before the uninstaller removes the legacy install dir and its registry entries.
            string? legacyInstallDir = Path.GetDirectoryName(legacyInstall.UninstallerPath);
            if (!string.IsNullOrEmpty(legacyInstallDir))
                LegacyIntegrationRepairService.RepairAutostartIfPointingToLegacyInstall(legacyInstallDir);
            LegacyIntegrationRepairService.RegisterOpenInLumisenseContextMenu();

            if (!LegacyInnoCleanupService.TryStartInteractiveUninstall(legacyInstall, out string? technicalError))
            {
                LocalizedMessageBox.Show(
                    LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupFailed),
                    LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupTitle),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                if (!string.IsNullOrWhiteSpace(technicalError))
                    Logger.Warn($"Legacy cleanup не был запущен: {technicalError}");
                return;
            }

            LocalizedMessageBox.Show(
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupStartingMessage),
                LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupStartingTitle),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось показать cleanup-flow первого запуска Velopack: {ex.Message}");
        }
    }
}
