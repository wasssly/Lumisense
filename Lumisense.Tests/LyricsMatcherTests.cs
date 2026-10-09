using System.Collections.Generic;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class LyricsMatcherTests
{
    private static OnlineLyricsResult Result(string title, string artist, double duration = 200, bool synced = true, string source = "LRCLIB", long id = 1) =>
        new(id, title, artist, string.Empty, duration, synced ? null : "plain", synced ? "[00:01.00]line" : null, source);

    [Theory]
    [InlineData("Song (feat. Someone)", "Song")]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Song [Live]", "Song")]
    [InlineData("Song - Radio Edit", "Song")]
    [InlineData("Song feat. Someone", "Song")]
    [InlineData("(I Can't Get No) Satisfaction", "(I Can't Get No) Satisfaction")]
    [InlineData("Remastered", "Remastered")]
    public void CleanTitle_RemovesVersionNoiseOnly(string title, string expected) =>
        Assert.Equal(expected, LyricsMatcher.CleanTitle(title));

    [Fact]
    public void SplitArtists_KeepsAmpersandInsideOneArtist()
    {
        Assert.Equal(new[] { "A", "B" }, LyricsMatcher.SplitArtists("A; B"));
        Assert.Equal(new[] { "Simon & Garfunkel" }, LyricsMatcher.SplitArtists("Simon & Garfunkel"));
        Assert.Equal(new[] { "A", "B" }, LyricsMatcher.SplitArtists("A feat. B"));
        Assert.Empty(LyricsMatcher.SplitArtists("—"));
    }

    [Fact]
    public void BuildQueryVariants_DoesNotRepeatIdenticalVariants()
    {
        var variants = LyricsMatcher.BuildQueryVariants("Song", "Artist");
        Assert.Equal(variants.Count, variants.Distinct().Count());
        Assert.Equal(("Song", "Artist"), variants[0]);
        Assert.Contains(("Song", string.Empty), variants);
    }

    [Fact]
    public void FindBest_MatchesDespiteFeaturingAndRemasterSuffix()
    {
        var results = new List<OnlineLyricsResult> { Result("Song", "Artist") };
        OnlineLyricsResult? best = LyricsMatcher.FindBest(results, "Song (feat. Guest) - Remastered 2011", "Artist; Guest", 200);
        Assert.NotNull(best);
    }

    [Fact]
    public void FindBest_RejectsDifferentArtist()
    {
        var results = new List<OnlineLyricsResult> { Result("Song", "Someone Else") };
        Assert.Null(LyricsMatcher.FindBest(results, "Song", "Artist", 200));
    }

    [Fact]
    public void FindBest_RejectsVeryDifferentDuration()
    {
        var results = new List<OnlineLyricsResult> { Result("Song", "Artist", duration: 260) };
        Assert.Null(LyricsMatcher.FindBest(results, "Song", "Artist", 200));
    }

    [Fact]
    public void FindBest_AcceptsUnknownDuration()
    {
        var results = new List<OnlineLyricsResult> { Result("Song", "Artist", duration: 0) };
        Assert.NotNull(LyricsMatcher.FindBest(results, "Song", "Artist", 200));
    }

    [Fact]
    public void FindBest_PrefersSyncedAndCloserDuration()
    {
        var plain = Result("Song", "Artist", duration: 200, synced: false, id: 1);
        var synced = Result("Song", "Artist", duration: 201, synced: true, source: "NetEase", id: 2);
        OnlineLyricsResult? best = LyricsMatcher.FindBest(new[] { plain, synced }, "Song", "Artist", 200);
        Assert.Equal(2, best?.Id);
    }

    [Fact]
    public void FindBest_MatchesCyrillicAndAmpersandArtists()
    {
        var results = new List<OnlineLyricsResult> { Result("Звезда по имени Солнце", "Кино") };
        Assert.NotNull(LyricsMatcher.FindBest(results, "Звезда по имени Солнце", "Кино", 0));

        var duo = new List<OnlineLyricsResult> { Result("Song", "A & B") };
        Assert.NotNull(LyricsMatcher.FindBest(duo, "Song", "A", 0));
    }

    [Fact]
    public void FindBest_RejectsShortContainedArtistKey()
    {
        var results = new List<OnlineLyricsResult> { Result("Song", "Edguy") };
        Assert.Null(LyricsMatcher.FindBest(results, "Song", "Ed", 0));
    }
}
