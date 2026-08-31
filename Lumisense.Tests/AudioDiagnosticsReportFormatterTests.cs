using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioDiagnosticsReportFormatterTests
{
    [Fact]
    public void Format_ContainsActualOutputDetailsForInitializedEndpoint()
    {
        var status = new AudioOutputRuntimeStatus(
            ActiveDeviceName: "USB DAC",
            FallbackFrom: null,
            IsInitialized: true,
            Engine: "WASAPI Shared · WasapiPlayer",
            RequestedLatencyMilliseconds: 60,
            ActualLatencyMilliseconds: 64,
            OutputFormat: "WaveFormat: 48000Hz 2 channels",
            ActiveEndpointId: "{endpoint-id}",
            PlaybackState: "Воспроизводится",
            FollowsSystemDefault: true,
            InitializationMilliseconds: 21,
            RecoveryCount: 1,
            LastRecoveryReason: "Windows изменил системное устройство вывода",
            MeaningfulDeviceEventCount: 2,
            LastDeviceEventKind: AudioOutputEndpointChangeKind.DefaultDeviceChanged,
            LastDeviceEventEndpointId: "{endpoint-id}");

        string report = AudioDiagnosticsReportFormatter.Format("1.18.0", status);

        Assert.Contains("version: 1.18.0", report);
        Assert.Contains("routing: windows-default", report);
        Assert.Contains("endpoint-id: {endpoint-id}", report);
        Assert.Contains("latency-requested-ms: 60", report);
        Assert.Contains("latency-actual-ms: 64", report);
        Assert.Contains("last-device-event: DefaultDeviceChanged", report);
    }

    [Fact]
    public void Format_UsesSafePlaceholdersBeforeOutputInitialization()
    {
        var status = new AudioOutputRuntimeStatus(
            ActiveDeviceName: "Системное устройство по умолчанию",
            FallbackFrom: null,
            IsInitialized: false,
            Engine: "WASAPI Shared · WasapiPlayer",
            RequestedLatencyMilliseconds: 60,
            ActualLatencyMilliseconds: null,
            OutputFormat: null,
            ActiveEndpointId: null,
            PlaybackState: "Не инициализирован",
            FollowsSystemDefault: false,
            InitializationMilliseconds: 0,
            RecoveryCount: 0,
            LastRecoveryReason: null,
            MeaningfulDeviceEventCount: 0,
            LastDeviceEventKind: null,
            LastDeviceEventEndpointId: null);

        string report = AudioDiagnosticsReportFormatter.Format("1.18.0", status);

        Assert.Contains("routing: fixed-endpoint", report);
        Assert.Contains("endpoint-id: n/a", report);
        Assert.Contains("output-format: n/a", report);
        Assert.Contains("latency-actual-ms: n/a", report);
        Assert.Contains("last-device-event: none", report);
    }
}
