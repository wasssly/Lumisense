namespace Lumisense;

/// <summary>
/// Состояние диагностики аудиовыхода: что выбрано и инициализировано, сколько было восстановлений и событий устройства.
/// Только хранит данные для отчёта и не принимает решений о воспроизведении.
/// </summary>
internal sealed class AudioOutputDiagnostics
{
    public string? ActiveFormat { get; set; }
    public long LastInitializationMilliseconds { get; set; }
    public int RecoveryCount { get; set; }
    public string? LastRecoveryReason { get; set; }
    public int MeaningfulDeviceEventCount { get; private set; }
    public AudioOutputEndpointChangeKind? LastDeviceEventKind { get; private set; }
    public string? LastDeviceEventEndpointId { get; private set; }

    // Отдельно от settings.json храним то, что реально открыл WASAPI: так Settings объяснит fallback после отключения
    // USB/Bluetooth-устройства, и пользователь не гадает, куда идёт звук.
    public string ActiveDeviceKey { get; set; } = AudioOutputDeviceService.SystemDefaultDeviceName;
    public string? FallbackFrom { get; set; }
    // Реально применённый режим WASAPI ("Shared"/"Exclusive") — может отличаться от
    // _settings.WasapiMode сразу после автоматического отката в EnsureOutputDevice.
    public string ActiveWasapiMode { get; set; } = "Shared";

    public void RecordDeviceEvent(AudioOutputEndpointChangeKind kind, string? endpointId)
    {
        MeaningfulDeviceEventCount++;
        LastDeviceEventKind = kind;
        LastDeviceEventEndpointId = endpointId;
    }

    // Новый endpoint: формат и время инициализации прежнего выхода больше не актуальны.
    public void ResetForNewEndpoint()
    {
        ActiveFormat = null;
        LastInitializationMilliseconds = 0;
    }
}
