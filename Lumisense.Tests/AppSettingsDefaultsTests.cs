using Lumisense;
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

    [Fact]
    public void NewProfile_UsesDefaultMiniPlayerArtworkProgressThickness()
    {
        Assert.Equal(2.5, new AppSettings().MiniPlayerArtworkProgressThickness, precision: 3);
    }
}
