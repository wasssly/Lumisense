namespace Lumisense;

/// <summary>
/// Форматирует технический аудиоотчёт для явного копирования пользователем. Отчёт намеренно
/// не принимает и не выводит путь, название или метаданные текущего трека.
/// </summary>
internal static class AudioDiagnosticsReportFormatter
{
    public static string Format(string version, AudioOutputRuntimeStatus status) =>
        string.Join(Environment.NewLine, new[]
        {
            "Lumisense audio diagnostics",
            $"version: {version}",
            $"engine: {status.Engine}",
            $"playback-state: {status.PlaybackState}",
            $"routing: {(status.FollowsSystemDefault ? "windows-default" : "fixed-endpoint")}",
            $"active-device: {status.ActiveDeviceName}",
            $"endpoint-id: {status.ActiveEndpointId ?? "n/a"}",
            $"output-format: {status.OutputFormat ?? "n/a"}",
            $"latency-requested-ms: {status.RequestedLatencyMilliseconds}",
            $"latency-actual-ms: {status.ActualLatencyMilliseconds?.ToString() ?? "n/a"}",
            $"initialization-ms: {status.InitializationMilliseconds}",
            $"recovery-count: {status.RecoveryCount}",
            $"last-recovery: {status.LastRecoveryReason ?? "none"}",
            $"meaningful-device-events: {status.MeaningfulDeviceEventCount}",
            $"last-device-event: {status.LastDeviceEventKind?.ToString() ?? "none"}",
            $"last-device-event-endpoint: {status.LastDeviceEventEndpointId ?? "n/a"}",
            status.SignalPath is null ? null : $"signal-path: {status.SignalPath.Text.Replace("\n", " ")}",
            status.SignalPath is null ? null : $"bit-perfect: {status.SignalPath.IsBitPerfect}"
        }.Where(line => line is not null));
}
