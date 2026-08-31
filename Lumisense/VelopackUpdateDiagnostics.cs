using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Velopack;
using Velopack.Logging;

namespace Lumisense;

/// <summary>
/// Данные, которые UpdateManager уже вычислил до загрузки. Это план, а не подтверждение
/// фактически переданного файла: DownloadUpdatesAsync сообщает приложению только процент.
/// </summary>
public sealed class VelopackUpdatePlan
{
    internal VelopackUpdatePlan(VelopackAsset fullPackage, IReadOnlyList<VelopackAsset> deltaPackages)
    {
        FullPackage = fullPackage;
        DeltaPackages = deltaPackages;
    }

    public VelopackAsset FullPackage { get; }
    public IReadOnlyList<VelopackAsset> DeltaPackages { get; }
    public bool HasDeltaPlan => DeltaPackages.Count > 0;
    public long DeltaBytes => DeltaPackages.Sum(asset => Math.Max(0, asset.Size));
}

/// <summary>
/// Сохраняет безопасные диагностические сведения в общий журнал Lumisense и готовит отчёт,
/// который пользователь может скопировать. Модуль намеренно не угадывает fallback: SDK 1.2.0
/// не передаёт выбранный asset, его байты либо причину переключения на full package.
/// </summary>
public enum VelopackUpdateStage
{
    Ready,
    Downloading,
    Paused,
    PreparingRestart,
    Cancelled,
    Failed,
}

public sealed class VelopackUpdateDiagnostics : IDisposable
{
    private readonly string _currentVersion;
    private readonly Stopwatch _timer = new();
    private bool _disposed;
    private int _lastLoggedProgress = -1;
    private int _progressPercentage;

    public VelopackUpdateStage Stage { get; private set; } = VelopackUpdateStage.Ready;
    public int ProgressPercentage => _progressPercentage;

    public VelopackUpdateDiagnostics(string currentVersion, UpdateInfo update)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "unknown" : currentVersion;
        Plan = CreatePlan(update);
    }

    public VelopackUpdatePlan Plan { get; }
    public TimeSpan Elapsed => _timer.Elapsed;
    public string ElapsedText => FormatElapsed(_timer.Elapsed);

    public static VelopackUpdatePlan CreatePlan(UpdateInfo update)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));
        var deltas = (update.DeltasToTarget ?? Array.Empty<VelopackAsset>())
            .Where(asset => asset is not null)
            .ToArray();
        return new VelopackUpdatePlan(update.TargetFullRelease, deltas);
    }

    public void RecordPlan()
    {
        Logger.Info($"Velopack plan: current={_currentVersion}; target={Plan.FullPackage.Version}; " +
                    $"full={Describe(Plan.FullPackage)}; candidate-deltas={DescribeAll(Plan.DeltaPackages)}. " +
                    "Actual package selection is controlled by Velopack at runtime.");
    }

    public void Start(bool resumed)
    {
        EnsureActive();
        // Pause в публичном API реализована отменой. При нажатии «Продолжить» SDK заново
        // проверяет операцию и не гарантирует сохранение прежней позиции, поэтому не переносим
        // старый процент как подтверждённый прогресс новой попытки.
        _timer.Restart();
        _progressPercentage = 0;
        _lastLoggedProgress = -1;
        Stage = VelopackUpdateStage.Downloading;
        Logger.Info(resumed
            ? "Velopack download retry started after pause; the public SDK does not expose a confirmed resume offset."
            : "Velopack download started; the public SDK provides percent only, without transfer bytes or speed.");
    }

    public void Pause()
    {
        if (_disposed) return;
        _timer.Stop();
        Stage = VelopackUpdateStage.Paused;
        Logger.Info($"Velopack download paused at {_progressPercentage}%; elapsed={FormatElapsed(_timer.Elapsed)}.");
    }

    public void Progress(int percentage)
    {
        if (_disposed) return;
        int value = Math.Clamp(percentage, 0, 100);
        _progressPercentage = value;
        if (value != 0 && value != 100 && value / 10 == _lastLoggedProgress / 10) return;
        _lastLoggedProgress = value;
        Logger.Info($"Velopack progress={value}%; elapsed={FormatElapsed(_timer.Elapsed)}.");
    }

    public void Prepared()
    {
        if (_disposed) return;
        _timer.Stop();
        _progressPercentage = 100;
        Stage = VelopackUpdateStage.PreparingRestart;
        Logger.Info($"Velopack target prepared: {Describe(Plan.FullPackage)}; elapsed={FormatElapsed(_timer.Elapsed)}. " +
                    "The SDK does not disclose whether this full package was reconstructed from delta or downloaded directly.");
    }

    public void Cancelled()
    {
        if (_disposed) return;
        _timer.Stop();
        Stage = VelopackUpdateStage.Cancelled;
        Logger.Info($"Velopack download cancelled at {_progressPercentage}%; elapsed={FormatElapsed(_timer.Elapsed)}.");
    }

    public void Failed(Exception exception)
    {
        if (_disposed) return;
        _timer.Stop();
        Stage = VelopackUpdateStage.Failed;
        Logger.Warn($"Velopack update failed after {FormatElapsed(_timer.Elapsed)}: {exception.GetType().Name}: {exception.Message}");
    }

    public string CreateReport()
    {
        var report = new StringBuilder();
        report.AppendLine("Lumisense Velopack update diagnostics");
        report.AppendLine("Delivery: managed Velopack update from the public GitHub Releases feed");
        report.AppendLine($"Current version: {_currentVersion}");
        report.AppendLine($"Target version: {Plan.FullPackage.Version}");
        report.AppendLine($"State: {Stage}");
        report.AppendLine($"Progress reported by SDK: {_progressPercentage}%");
        report.AppendLine($"Full package: {Describe(Plan.FullPackage)}");
        if (Plan.HasDeltaPlan)
        {
            report.AppendLine($"Candidate delta packages ({Plan.DeltaPackages.Count}; {FormatBytes(Plan.DeltaBytes)}):");
            foreach (var delta in Plan.DeltaPackages) report.AppendLine($"- {Describe(delta)}");
            report.AppendLine("Plan: Velopack may try these delta packages before using the full package.");
        }
        else
        {
            report.AppendLine("Plan: no candidate delta package was supplied; a full package is expected.");
        }

        report.AppendLine("Note: the public Velopack API does not expose the actual transferred file, transfer bytes, speed, or fallback reason.");
        report.AppendLine($"Elapsed in this dialog: {FormatElapsed(_timer.Elapsed)}");
        return report.ToString();
    }

    public static void OpenVelopackLogsFolder()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "velopack");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось открыть папку журналов Velopack: {ex.Message}");
        }
    }

    public static string FormatBytes(long bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        if (bytes >= mib) return $"{bytes / mib:F1} MiB";
        if (bytes >= kib) return $"{bytes / kib:F0} KiB";
        return $"{bytes} B";
    }

    private static string Describe(VelopackAsset asset) =>
        $"{asset.FileName} ({FormatBytes(Math.Max(0, asset.Size))})";

    private static string DescribeAll(IEnumerable<VelopackAsset> assets)
    {
        string[] values = assets.Select(Describe).ToArray();
        return values.Length == 0 ? "none" : string.Join(" | ", values);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }

    private void EnsureActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VelopackUpdateDiagnostics));
    }
}

/// <summary>Добавляет сообщения штатного Velopack в безопасный журнал Lumisense.</summary>
internal sealed class LumisenseVelopackLogger : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        string line = $"Velopack [{logLevel}]: {message ?? "(no message)"}";
        if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
        Logger.Info(line);
    }
}
