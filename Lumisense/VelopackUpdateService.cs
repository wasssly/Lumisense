using System;
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

    private readonly UpdateManager _manager = new(
        new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
        new UpdateOptions { ExplicitChannel = ReleaseChannel });

    public bool IsManagedInstall => _manager.IsInstalled;

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
        Logger.Info(IsVelopackManagedInstall()
            ? "Режим обновлений: Velopack (доступны full/delta пакеты)."
            : "Режим обновлений: legacy Inno Setup (сохраняется проверенный SHA-256 установщик).");
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
