using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class HotkeyConfigurationTests
{
    [Fact]
    public void NewGlobalHotkeys_HaveDistinctCtrlAltDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal("F", settings.HotkeyToggleFavorite.Key);
        Assert.Equal("L", settings.HotkeyToggleLyrics.Key);
        Assert.Equal("N", settings.HotkeyToggleMiniPlayer.Key);

        Assert.True(settings.HotkeyToggleFavorite.Ctrl && settings.HotkeyToggleFavorite.Alt);
        Assert.True(settings.HotkeyToggleLyrics.Ctrl && settings.HotkeyToggleLyrics.Alt);
        Assert.True(settings.HotkeyToggleMiniPlayer.Ctrl && settings.HotkeyToggleMiniPlayer.Alt);
    }
}
