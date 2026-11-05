using System;
using System.Collections.Generic;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public class LyricsPresetTests
{
    [Fact]
    public void BuiltInPresets_HaveUniqueNames()
    {
        var names = LyricsPreset.BuiltIn.Select(preset => preset.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Apple Music", names);
    }

    [Fact]
    public void ApplyTo_ThenCapture_RoundTripsAllValues()
    {
        LyricsPreset apple = LyricsPreset.BuiltIn.First(preset => preset.Name == "Apple Music");
        var settings = new AppSettings();

        apple.ApplyTo(settings);

        Assert.Equal("Apple Music", settings.LyricsPresetName);
        Assert.Equal(LyricsTransitionStyle.AppleMusic, settings.SyncedLyricsTransition);
        Assert.True(settings.SyncedLyricsBlurInactive);
        Assert.True(LyricsPreset.Capture(settings, "x").HasSameValues(apple));
    }

    [Fact]
    public void HasSameValues_DetectsManualChange()
    {
        LyricsPreset classic = LyricsPreset.BuiltIn[0];
        var settings = new AppSettings();
        classic.ApplyTo(settings);
        settings.SyncedLyricsFontSize = 19;

        Assert.False(LyricsPreset.Capture(settings, "x").HasSameValues(classic));
    }

    [Fact]
    public void ApplyTo_ClampsOutOfRangeValues()
    {
        var wild = new LyricsPreset { Name = "Wild", SyncedLyricsFontSize = 99, SyncedLyricsScalePercent = 500, SyncedLyricsVisibleLines = 4, SyncedLyricsTransition = "nonsense" };
        var settings = new AppSettings();

        wild.ApplyTo(settings);

        Assert.Equal(20, settings.SyncedLyricsFontSize);
        Assert.Equal(LyricsHighlightStyle.MaxScalePercent, settings.SyncedLyricsScalePercent);
        Assert.Equal(5, settings.SyncedLyricsVisibleLines);
        Assert.Equal(LyricsTransitionStyle.Smooth, settings.SyncedLyricsTransition);
    }

    [Fact]
    public void TransitionStyle_UnknownFallsBackToSmooth_AndStaggerIsCapped()
    {
        Assert.Equal(LyricsTransitionStyle.Smooth, LyricsTransitionStyle.Normalize("?"));
        Assert.Equal(TimeSpan.Zero, LyricsTransitionStyle.LineDelay(LyricsTransitionStyle.Smooth, 5));
        Assert.Equal(LyricsTransitionStyle.LineDelay(LyricsTransitionStyle.AppleMusic, LyricsTransitionStyle.MaxStaggerLines),
            LyricsTransitionStyle.LineDelay(LyricsTransitionStyle.AppleMusic, 50));
        Assert.Equal(0, LyricsTransitionStyle.ScrollMs(LyricsTransitionStyle.None, 520));
    }
}
