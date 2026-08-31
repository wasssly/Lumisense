namespace Lumisense;

/// <summary>
/// Единый снимок фактического состояния WASAPI output для Settings и диагностических логов.
/// Поля отражают уже созданный WasapiPlayer, а не только сохранённый пользовательский выбор.
/// </summary>
internal sealed record AudioOutputRuntimeStatus(
    string ActiveDeviceName,
    string? FallbackFrom,
    bool IsInitialized,
    string Engine,
    int RequestedLatencyMilliseconds,
    int? ActualLatencyMilliseconds,
    string? OutputFormat,
    string? ActiveEndpointId,
    string PlaybackState,
    bool FollowsSystemDefault,
    long InitializationMilliseconds,
    int RecoveryCount,
    string? LastRecoveryReason,
    int MeaningfulDeviceEventCount,
    AudioOutputEndpointChangeKind? LastDeviceEventKind,
    string? LastDeviceEventEndpointId);
