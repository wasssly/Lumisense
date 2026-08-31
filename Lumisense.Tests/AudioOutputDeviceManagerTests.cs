using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioOutputDeviceManagerTests
{
    [Fact]
    public void GetFallbackSourceKey_ReturnsRequestedKeyOnlyForFallback()
    {
        Assert.Equal("wasapi:usb-endpoint", AudioOutputDeviceManager.GetFallbackSourceKey(
            "wasapi:usb-endpoint", usedFallback: true));
        Assert.Null(AudioOutputDeviceManager.GetFallbackSourceKey(
            "wasapi:usb-endpoint", usedFallback: false));
        Assert.Null(AudioOutputDeviceManager.GetFallbackSourceKey(null, usedFallback: true));
    }

    [Fact]
    public void ShouldPersistActiveKey_DoesNotOverwriteSelectionDuringFallback()
    {
        Assert.False(AudioOutputDeviceManager.ShouldPersistActiveKey(
            "wasapi:missing", "", usedFallback: true));
        Assert.True(AudioOutputDeviceManager.ShouldPersistActiveKey(
            "legacy-device", "wasapi:active", usedFallback: false));
        Assert.False(AudioOutputDeviceManager.ShouldPersistActiveKey(
            "wasapi:active", "wasapi:active", usedFallback: false));
    }

    [Fact]
    public void ShouldRestoreSavedEndpoint_RequiresFallbackAndDifferentSavedKey()
    {
        Assert.True(AudioOutputDeviceManager.ShouldRestoreSavedEndpoint(
            "wasapi:usb", "", outputIsFallback: true));
        Assert.False(AudioOutputDeviceManager.ShouldRestoreSavedEndpoint(
            "wasapi:usb", "wasapi:usb", outputIsFallback: true));
        Assert.False(AudioOutputDeviceManager.ShouldRestoreSavedEndpoint(
            "wasapi:usb", "", outputIsFallback: false));
        Assert.False(AudioOutputDeviceManager.ShouldRestoreSavedEndpoint(
            null, "", outputIsFallback: true));
    }
}
