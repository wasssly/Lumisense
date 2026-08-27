using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AppSettingsDefaultsTests
{
    [Fact]
    public void NewProfile_UsesFifteenPercentRememberedVolume()
    {
        var settings = new AppSettings();

        Assert.True(settings.RememberVolume);
        Assert.Equal(0.15, settings.SavedVolume, precision: 3);
    }
}
