using Xunit;

namespace Lumisense.Tests;

public sealed class LyricsHighlightStyleTests
{
    [Theory]
    [InlineData("Glow", true, false, false)]
    [InlineData("GlowPill", true, true, false)]
    [InlineData("Pill", false, true, false)]
    [InlineData("Bar", false, false, true)]
    [InlineData("None", false, false, false)]
    public void Style_DescribesWhichEffectsAreDrawn(string effect, bool glow, bool pill, bool bar)
    {
        Assert.Equal(glow, LyricsHighlightStyle.HasGlow(effect));
        Assert.Equal(pill, LyricsHighlightStyle.HasPill(effect));
        Assert.Equal(bar, LyricsHighlightStyle.HasBar(effect));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Scale")]
    [InlineData("GlowScale")]
    public void LegacyAndMissingValues_AreReadAsSoftGlow(string? effect)
    {
        Assert.True(LyricsHighlightStyle.HasGlow(effect));
        Assert.False(LyricsHighlightStyle.HasPill(effect));
        Assert.False(LyricsHighlightStyle.HasBar(effect));
    }

    [Fact]
    public void NewSettings_AreOffByDefault_AndDefaultEffectIsGlowWithoutBackdrop()
    {
        var settings = new AppSettings();

        Assert.Equal("Glow", settings.SyncedLyricsHighlightEffect);
        Assert.False(settings.SyncedLyricsScaleActiveLine);
        Assert.False(settings.SyncedLyricsFadeDistantLines);
        Assert.False(LyricsHighlightStyle.HasPill(settings.SyncedLyricsHighlightEffect));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 3)]
    [InlineData(4, 5)]
    [InlineData(8, 9)]
    [InlineData(15, 15)]
    [InlineData(40, 15)]
    [InlineData(-5, 0)]
    public void NormalizeVisibleLines_GivesZeroOrOddCountUpToLimit(int input, int expected)
    {
        Assert.Equal(expected, LyricsHighlightStyle.NormalizeVisibleLines(input));
    }

    [Fact]
    public void VisibleRadius_IsHalfOfWindow_AndUnlimitedForAllLines()
    {
        Assert.Equal(1, LyricsHighlightStyle.VisibleRadius(3));
        Assert.Equal(7, LyricsHighlightStyle.VisibleRadius(15));
        Assert.Equal(int.MaxValue, LyricsHighlightStyle.VisibleRadius(0));
    }

    [Fact]
    public void FadeStep_GrowsWithStrength_AndKeepsPreviousDefault()
    {
        Assert.Equal(0.10, LyricsHighlightStyle.FadeStep(50), 3);
        Assert.True(LyricsHighlightStyle.FadeStep(100) > LyricsHighlightStyle.FadeStep(0));
        Assert.Equal(LyricsHighlightStyle.FadeStep(100), LyricsHighlightStyle.FadeStep(500), 6);
    }

    [Fact]
    public void ScaleFactor_AndPadding_AreClampedAndKeepPreviousDefault()
    {
        Assert.Equal(1.05, LyricsHighlightStyle.ScaleFactor(5), 3);
        Assert.Equal(1.15, LyricsHighlightStyle.ScaleFactor(99), 3);
        Assert.Equal(1.02, LyricsHighlightStyle.ScaleFactor(0), 3);
        Assert.Equal(46, LyricsHighlightStyle.ScaleSidePadding(5), 3);
        Assert.True(LyricsHighlightStyle.ScaleSidePadding(15) > LyricsHighlightStyle.ScaleSidePadding(2));
    }

    [Fact]
    public void NewDegreeSettings_HaveDefaultsThatMatchEarlierBehaviour()
    {
        var settings = new AppSettings();

        Assert.Equal(50, settings.SyncedLyricsFadeStrength);
        Assert.Equal(5, settings.SyncedLyricsScalePercent);
        Assert.Equal(0, settings.SyncedLyricsVisibleLines);
    }
}
