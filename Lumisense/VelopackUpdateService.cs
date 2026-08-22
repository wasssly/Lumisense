using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
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

    private readonly UpdateManager _manager;

    public VelopackUpdateService()
    {
        _manager = CreateManager(out bool usesLocalTestFeed);
        UsesLocalTestFeed = usesLocalTestFeed;
    }

    public bool IsManagedInstall => _manager.IsInstalled;
    public bool UsesLocalTestFeed { get; }

    private static UpdateManager CreateManager(out bool usesLocalTestFeed)
    {
        var options = new UpdateOptions { ExplicitChannel = ReleaseChannel };

#if VELOPACK_LOCAL_FEED_TEST
        string? localFeedPath = Environment.GetEnvironmentVariable(LocalTestFeedEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(localFeedPath))
        {
            usesLocalTestFeed = true;
            return new UpdateManager(new SimpleFileSource(new DirectoryInfo(localFeedPath)), options);
        }
#endif

        usesLocalTestFeed = false;
        return new UpdateManager(
            new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
            options);
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

    public void ApplyAndRestart(UpdateInfo update)
    {
        if (!IsManagedInstall)
            throw new InvalidOperationException("Нельзя применить Velopack-обновление к старой Inno Setup-установке.");

        _manager.ApplyUpdatesAndRestart(update);
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
    /// Показывает пояснение только после подтверждённого первого запуска MSI/Velopack-копии.
    /// Важно: migration не удаляет Inno Setup автоматически — пользователь сначала проверяет
    /// новую копию, а затем при желании удаляет старую через Windows Installed apps.
    /// </summary>
    public static void TryShowFirstRunNotice()
    {
        if (!IsVelopackManagedInstall() || !VelopackMigrationLifecycle.TryConsumeFirstRunMarker())
            return;

        try
        {
            LocalizedMessageBox.Show(
                LocalizationService.Get(LocalizationKey.UpdateVelopackFirstRunMessage),
                LocalizationService.Get(LocalizationKey.UpdateVelopackFirstRunTitle),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось показать уведомление первого запуска Velopack: {ex.Message}");
        }
    }
}
