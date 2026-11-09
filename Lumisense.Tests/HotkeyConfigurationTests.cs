using System.Text.Json;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class HotkeyConfigurationTests
{
    [Fact]
    public void NewInstall_HasNoGlobalHotkeysAssigned()
    {
        var settings = new AppSettings();

        HotkeyBinding[] all =
        [
            settings.HotkeyPlayPause, settings.HotkeyNext, settings.HotkeyPrevious, settings.HotkeyStop,
            settings.HotkeyVolumeUp, settings.HotkeyVolumeDown, settings.HotkeyMute, settings.HotkeyShuffle,
            settings.HotkeyRepeat, settings.HotkeyToggleFavorite, settings.HotkeyToggleLyrics,
            settings.HotkeyToggleMiniPlayer, settings.HotkeySeekForward, settings.HotkeySeekBackward,
            settings.HotkeyDeleteTrack
        ];

        Assert.All(all, binding => Assert.True(binding.IsEmpty));
    }

    [Fact]
    public void SavedHotkeys_AreKeptWhenSettingsAreLoaded()
    {
        const string json = "{\"HotkeyPlayPause\":{\"Ctrl\":true,\"Alt\":true,\"Key\":\"P\"}}";

        AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("P", loaded.HotkeyPlayPause.Key);
        Assert.True(loaded.HotkeyPlayPause.Ctrl && loaded.HotkeyPlayPause.Alt);
        Assert.True(loaded.HotkeyNext.IsEmpty);
    }
}
