using System.Collections.Generic;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class LumiProfileEqualizerPresetsTests
{
    private static AppSettings WithPresets(params EqualizerPreset[] presets) =>
        new() { EqualizerPresets = new List<EqualizerPreset>(presets) };

    private static EqualizerPreset Preset(string name, params double[] gains) => new() { Name = name, GainsDb = gains };

    [Fact]
    public void Apply_AddsImportedPresetsAndKeepsExistingOnes()
    {
        var live = WithPresets(Preset("Mine", 1, 2, 3));
        var imported = WithPresets(Preset("Bass", 6, 4, 0));

        LumiProfileIO.Apply(imported, live);

        Assert.Equal(new[] { "Mine", "Bass" }, live.EqualizerPresets.Select(p => p.Name));
    }

    [Fact]
    public void Apply_SkipsIdenticalPreset_WhenImportedTwice()
    {
        var live = WithPresets();
        var imported = WithPresets(Preset("Bass", 6, 4, 0));

        LumiProfileIO.Apply(imported, live);
        LumiProfileIO.Apply(imported, live);

        Assert.Single(live.EqualizerPresets);
    }

    [Fact]
    public void Apply_RenamesPresetWithSameNameButDifferentGains()
    {
        var live = WithPresets(Preset("Bass", 1, 1, 1));
        var imported = WithPresets(Preset("Bass", 6, 4, 0));

        LumiProfileIO.Apply(imported, live);

        Assert.Equal(new[] { "Bass", "Bass (2)" }, live.EqualizerPresets.Select(p => p.Name));
    }

    [Fact]
    public void Apply_ClampsGainsToFilterRange()
    {
        var live = WithPresets();
        var imported = WithPresets(Preset("Loud", 99, -99));

        LumiProfileIO.Apply(imported, live);

        Assert.Equal(new[] { EqualizerSampleProvider.MaxGainDb, EqualizerSampleProvider.MinGainDb }, live.EqualizerPresets[0].GainsDb);
    }
}
