using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class DeviceSoundProfilesTests
{
    [Fact]
    public void Capture_StoresCurrentSettingsAndReusesTheProfile()
    {
        var settings = new AppSettings { EqualizerEnabled = true, PlaybackBalance = -20 };
        settings.EqualizerBandGainsDb[2] = 4;

        var first = DeviceSoundProfiles.Capture(settings, "dev-1", "Наушники");
        settings.PlaybackBalance = 10;
        var second = DeviceSoundProfiles.Capture(settings, "dev-1", "Наушники");

        Assert.Same(first, second);
        Assert.Single(settings.DeviceSoundProfiles);
        Assert.Equal(10, second.PlaybackBalance);
        Assert.Equal(4, second.EqualizerBandGainsDb[2]);
    }

    [Fact]
    public void Apply_RestoresValuesAndClampsOutOfRangeData()
    {
        var settings = new AppSettings();
        var profile = new DeviceSoundProfile
        {
            EqualizerEnabled = true,
            PlaybackBalance = 500,
            EqualizerBandGainsDb = new[] { 99.0, double.NaN, -50.0 }
        };

        DeviceSoundProfiles.Apply(settings, profile);

        Assert.True(settings.EqualizerEnabled);
        Assert.Equal(100, settings.PlaybackBalance);
        Assert.Equal(10, settings.EqualizerBandGainsDb.Length);
        Assert.Equal(EqualizerSampleProvider.MaxGainDb, settings.EqualizerBandGainsDb[0]);
        Assert.Equal(0, settings.EqualizerBandGainsDb[1]);
        Assert.Equal(EqualizerSampleProvider.MinGainDb, settings.EqualizerBandGainsDb[2]);
    }

    [Fact]
    public void Capture_DropsOldestProfilesOverTheLimit()
    {
        var settings = new AppSettings();
        for (int i = 0; i < DeviceSoundProfiles.MaxProfiles + 3; i++)
            DeviceSoundProfiles.Capture(settings, $"dev-{i}", "d");

        Assert.Equal(DeviceSoundProfiles.MaxProfiles, settings.DeviceSoundProfiles.Count);
        Assert.Null(DeviceSoundProfiles.Find(settings, "dev-0"));
        Assert.NotNull(DeviceSoundProfiles.Find(settings, $"dev-{DeviceSoundProfiles.MaxProfiles + 2}"));
    }
}
