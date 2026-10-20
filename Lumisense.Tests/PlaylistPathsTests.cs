using System.Collections.Generic;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class PlaylistPathsTests
{
    [Fact]
    public void IndexOf_FindsPathIgnoringCase()
    {
        var paths = new List<string> { @"C:\Music\a.mp3", @"C:\Music\B.mp3" };

        Assert.Equal(1, PlaylistPaths.IndexOf(paths, @"c:\music\b.MP3"));
    }

    [Fact]
    public void IndexOf_ReturnsMinusOne_ForMissingOrNullPath()
    {
        var paths = new List<string> { "a.mp3" };

        Assert.Equal(-1, PlaylistPaths.IndexOf(paths, "other.mp3"));
        Assert.Equal(-1, PlaylistPaths.IndexOf(paths, null));
        Assert.Equal(-1, PlaylistPaths.IndexOf(new List<string>(), "a.mp3"));
    }

    [Fact]
    public void ToSet_TreatsDifferentCaseAsSameFile()
    {
        HashSet<string> set = PlaylistPaths.ToSet(new[] { @"C:\Music\a.mp3" });

        Assert.Contains(@"c:\MUSIC\A.mp3", set);
        // Add возвращает false для повтора — на этом строится отсев дублей внутри одной выборки.
        Assert.False(set.Add(@"c:\music\a.mp3"));
        Assert.True(set.Add(@"C:\Music\b.mp3"));
    }

    [Fact]
    public void Equal_IgnoresCase_AndHandlesNull()
    {
        Assert.True(PlaylistPaths.Equal("A.mp3", "a.MP3"));
        Assert.False(PlaylistPaths.Equal("a.mp3", null));
        Assert.True(PlaylistPaths.Equal(null, null));
    }

    [Fact]
    public void FindNeighborAvailable_Forward_SkipsMissingTracksAfterCurrent()
    {
        var full = new List<string> { "a.mp3", "b.mp3", "c.mp3", "d.mp3" };
        var available = PlaylistPaths.ToSet(new[] { "a.mp3", "d.mp3" });

        // b.mp3 исчез: «Далее» идёт к следующему по порядку доступному (d), а не к первому треку.
        Assert.Equal("d.mp3", PlaylistPaths.FindNeighborAvailable(full, "b.mp3", available, +1));
    }

    [Fact]
    public void FindNeighborAvailable_Backward_GoesToPreviousAvailable()
    {
        var full = new List<string> { "a.mp3", "b.mp3", "c.mp3", "d.mp3" };
        var available = PlaylistPaths.ToSet(new[] { "a.mp3", "d.mp3" });

        Assert.Equal("a.mp3", PlaylistPaths.FindNeighborAvailable(full, "c.mp3", available, -1));
    }

    [Fact]
    public void FindNeighborAvailable_WrapsAround()
    {
        var full = new List<string> { "a.mp3", "b.mp3", "c.mp3" };
        var available = PlaylistPaths.ToSet(new[] { "a.mp3" });

        Assert.Equal("a.mp3", PlaylistPaths.FindNeighborAvailable(full, "c.mp3", available, +1));
        // Назад от первого трека тоже идёт по кругу, а регистр пути текущего трека не важен.
        Assert.Equal("c.mp3", PlaylistPaths.FindNeighborAvailable(full, "A.MP3", PlaylistPaths.ToSet(new[] { "c.mp3" }), -1));
    }

    [Fact]
    public void FindNeighborAvailable_ReturnsNull_WhenCurrentUnknownOrNothingAvailable()
    {
        var full = new List<string> { "a.mp3", "b.mp3" };

        Assert.Null(PlaylistPaths.FindNeighborAvailable(full, "zzz.mp3", PlaylistPaths.ToSet(new[] { "a.mp3" }), +1));
        Assert.Null(PlaylistPaths.FindNeighborAvailable(full, "a.mp3", PlaylistPaths.ToSet(new string[0]), +1));
    }
}
