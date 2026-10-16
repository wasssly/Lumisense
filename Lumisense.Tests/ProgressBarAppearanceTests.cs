using Color = System.Windows.Media.Color;
using Xunit;

namespace Lumisense.Tests;

public sealed class ProgressBarAppearanceTests
{
    [Fact]
    public void Defaults_ReproduceTheOriginalLook()
    {
        var appearance = new ProgressBarAppearance();

        Assert.Equal("Accent", appearance.ColorMode);
        Assert.Equal(1.0, appearance.TrackThickness);
        Assert.Equal(1.0, appearance.ThumbSize);
        Assert.False(appearance.IsFlat);
        Assert.False(appearance.WaveAlways);
        Assert.True(appearance.HoverTime);
        Assert.True(appearance.HoverHighlight);
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeAndBrokenValues()
    {
        var appearance = new ProgressBarAppearance
        {
            ColorMode = "Rainbow",
            ColorHex = "nope",
            EndShape = "Spiky",
            TrackThickness = 9,
            ThumbSize = 0,
            WaveAmplitude = double.NaN,
            WaveLength = 0.1,
            WaveSpeed = -3,
            WaveformGap = 5,
            WaveformUnplayedOpacity = 0
        };

        appearance.Normalize();

        Assert.Equal("Accent", appearance.ColorMode);
        Assert.Equal("#0078D4", appearance.ColorHex);
        Assert.Equal("Round", appearance.EndShape);
        Assert.Equal(2.0, appearance.TrackThickness);
        Assert.Equal(0.6, appearance.ThumbSize);
        Assert.Equal(1.0, appearance.WaveAmplitude);
        Assert.Equal(0.5, appearance.WaveLength);
        Assert.Equal(0.0, appearance.WaveSpeed);
        Assert.Equal(0.7, appearance.WaveformGap);
        Assert.Equal(0.3, appearance.WaveformUnplayedOpacity);
    }

    [Fact]
    public void Clone_IsIndependentOfTheOriginal()
    {
        var original = new ProgressBarAppearance { TrackThickness = 1.5 };
        ProgressBarAppearance copy = original.Clone();
        copy.TrackThickness = 0.5;

        Assert.Equal(1.5, original.TrackThickness);
    }
}

public sealed class ProgressAppearanceApplierTests
{
    [Fact]
    public void For_UsesTheSharedProfileUnlessEachWindowHasItsOwn()
    {
        var settings = new AppSettings();
        settings.ProgressAppearanceMain.TrackThickness = 1.2;
        settings.ProgressAppearanceMini.TrackThickness = 1.8;

        Assert.Equal(1.2, ProgressAppearanceApplier.For(settings, ProgressSurface.Mini).TrackThickness);

        settings.ProgressAppearanceScope = "PerWindow";

        Assert.Equal(1.8, ProgressAppearanceApplier.For(settings, ProgressSurface.Mini).TrackThickness);
        Assert.Equal(1.2, ProgressAppearanceApplier.For(settings, ProgressSurface.Main).TrackThickness);
        Assert.Equal(1.0, ProgressAppearanceApplier.For(settings, ProgressSurface.NowPlaying).TrackThickness);
    }

    [Fact]
    public void AnyUsesCover_FollowsTheProfilesInUse()
    {
        var settings = new AppSettings();
        settings.ProgressAppearanceMini.ColorMode = "Cover";

        Assert.False(ProgressAppearanceApplier.AnyUsesCover(settings));

        settings.ProgressAppearanceScope = "PerWindow";

        Assert.True(ProgressAppearanceApplier.AnyUsesCover(settings));
    }

    [Fact]
    public void ResolveColor_FallsBackToTheAccentWhenTheChosenColorIsMissing()
    {
        Color accent = Color.FromRgb(0, 120, 212);
        Color cover = Color.FromRgb(200, 30, 60);

        Assert.Equal(accent, ProgressAppearanceApplier.ResolveColor(new ProgressBarAppearance(), accent, cover));
        Assert.Equal(cover, ProgressAppearanceApplier.ResolveColor(new ProgressBarAppearance { ColorMode = "Cover" }, accent, cover));
        Assert.Equal(accent, ProgressAppearanceApplier.ResolveColor(new ProgressBarAppearance { ColorMode = "Cover" }, accent, null));
        Assert.Equal(Color.FromRgb(0xFF, 0x8C, 0x00),
            ProgressAppearanceApplier.ResolveColor(new ProgressBarAppearance { ColorMode = "Custom", ColorHex = "#FF8C00" }, accent, cover));
        Assert.Equal(accent,
            ProgressAppearanceApplier.ResolveColor(new ProgressBarAppearance { ColorMode = "Custom", ColorHex = "bad" }, accent, cover));
    }
}
