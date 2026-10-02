using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioOutputDiagnosticsTests
{
    [Fact]
    public void NewInstance_StartsWithSystemDefaultSharedAndNoHistory()
    {
        var diagnostics = new AudioOutputDiagnostics();

        Assert.Equal(AudioOutputDeviceService.SystemDefaultDeviceName, diagnostics.ActiveDeviceKey);
        Assert.Equal("Shared", diagnostics.ActiveWasapiMode);
        Assert.Null(diagnostics.ActiveFormat);
        Assert.Null(diagnostics.FallbackFrom);
        Assert.Equal(0, diagnostics.RecoveryCount);
        Assert.Equal(0, diagnostics.MeaningfulDeviceEventCount);
        Assert.Null(diagnostics.LastDeviceEventKind);
        Assert.Null(diagnostics.LastDeviceEventEndpointId);
    }

    [Fact]
    public void RecordDeviceEvent_CountsEventsAndKeepsTheLastOne()
    {
        var diagnostics = new AudioOutputDiagnostics();

        diagnostics.RecordDeviceEvent(AudioOutputEndpointChangeKind.DeviceAdded, "endpoint-a");
        diagnostics.RecordDeviceEvent(AudioOutputEndpointChangeKind.DefaultDeviceChanged, "endpoint-b");

        Assert.Equal(2, diagnostics.MeaningfulDeviceEventCount);
        Assert.Equal(AudioOutputEndpointChangeKind.DefaultDeviceChanged, diagnostics.LastDeviceEventKind);
        Assert.Equal("endpoint-b", diagnostics.LastDeviceEventEndpointId);
    }

    [Fact]
    public void RecordDeviceEvent_AcceptsMissingEndpointId()
    {
        var diagnostics = new AudioOutputDiagnostics();

        diagnostics.RecordDeviceEvent(AudioOutputEndpointChangeKind.DeviceRemoved, "endpoint-a");
        diagnostics.RecordDeviceEvent(AudioOutputEndpointChangeKind.DeviceStateChanged, null);

        Assert.Equal(2, diagnostics.MeaningfulDeviceEventCount);
        Assert.Null(diagnostics.LastDeviceEventEndpointId);
    }

    [Fact]
    public void ResetForNewEndpoint_ClearsFormatAndInitializationTimeOnly()
    {
        var diagnostics = new AudioOutputDiagnostics
        {
            ActiveFormat = "48 kHz  2 ch  32-bit",
            LastInitializationMilliseconds = 120,
            RecoveryCount = 3,
            LastRecoveryReason = "device removed",
            FallbackFrom = "usb-device"
        };
        diagnostics.RecordDeviceEvent(AudioOutputEndpointChangeKind.DeviceRemoved, "endpoint-a");

        diagnostics.ResetForNewEndpoint();

        Assert.Null(diagnostics.ActiveFormat);
        Assert.Equal(0, diagnostics.LastInitializationMilliseconds);
        Assert.Equal(3, diagnostics.RecoveryCount);
        Assert.Equal("device removed", diagnostics.LastRecoveryReason);
        Assert.Equal("usb-device", diagnostics.FallbackFrom);
        Assert.Equal(1, diagnostics.MeaningfulDeviceEventCount);
    }
}
