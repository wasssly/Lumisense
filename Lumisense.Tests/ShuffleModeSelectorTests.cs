using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Lumisense.Tests;

public sealed class ShuffleModeSelectorTests
{
    private static readonly string[] Tracks = { @"C:\m\a\1.mp3", @"C:\m\a\2.mp3", @"C:\m\a\3.mp3", @"C:\m\b\1.mp3", @"C:\m\b\2.mp3", @"C:\m\c\1.mp3" };

    [Theory]
    [InlineData("Deck", "Deck")]
    [InlineData("Random", "Random")]
    [InlineData("Favorites", "Favorites")]
    [InlineData("Rare", "Rare")]
    [InlineData("Folders", "Folders")]
    [InlineData("unknown", "Deck")]
    [InlineData(null, "Deck")]
    public void Normalize_FallsBackToDeck(string? value, string expected) =>
        Assert.Equal(expected, ShuffleModes.Normalize(value));

    [Theory]
    [InlineData(ShuffleModes.Deck)]
    [InlineData(ShuffleModes.Random)]
    [InlineData(ShuffleModes.Favorites)]
    [InlineData(ShuffleModes.Rare)]
    [InlineData(ShuffleModes.Folders)]
    public void NeverRepeatsTheCurrentTrackWhileOthersExist(string mode)
    {
        var random = new Random(7);
        var bag = new List<string>();
        string current = Tracks[0];
        for (int i = 0; i < 300; i++)
        {
            string next = ShuffleModeSelector.TakeNext(mode, bag, Tracks, current, random)!;
            Assert.NotEqual(current, next);
            current = next;
        }
    }

    [Theory]
    [InlineData(ShuffleModes.Random)]
    [InlineData(ShuffleModes.Favorites)]
    [InlineData(ShuffleModes.Rare)]
    [InlineData(ShuffleModes.Folders)]
    public void SingleTrackPlaylist_ReturnsThatTrack(string mode) =>
        Assert.Equal(Tracks[0], ShuffleModeSelector.TakeNext(mode, new List<string>(), new[] { Tracks[0] }, Tracks[0], new Random(1)));

    [Fact]
    public void EmptyPlaylist_ReturnsNull() =>
        Assert.Null(ShuffleModeSelector.TakeNext(ShuffleModes.Random, new List<string>(), Array.Empty<string>(), null, new Random(1)));

    [Fact]
    public void Favorites_ComeUpMoreOftenThanOtherTracks()
    {
        var random = new Random(11);
        string favorite = Tracks[3];
        var counts = Tracks.ToDictionary(path => path, _ => 0);
        for (int i = 0; i < 6000; i++)
        {
            string next = ShuffleModeSelector.TakeNext(ShuffleModes.Favorites, new List<string>(), Tracks, null, random,
                isFavorite: path => path == favorite)!;
            counts[next]++;
        }

        double averageOther = counts.Where(pair => pair.Key != favorite).Average(pair => pair.Value);
        Assert.True(counts[favorite] > averageOther * 2.2, $"favorite={counts[favorite]}, other={averageOther}");
    }

    [Fact]
    public void Rare_PrefersTracksWithFewerPlays()
    {
        var random = new Random(5);
        string neverPlayed = Tracks[5];
        var counts = Tracks.ToDictionary(path => path, _ => 0);
        for (int i = 0; i < 6000; i++)
        {
            string next = ShuffleModeSelector.TakeNext(ShuffleModes.Rare, new List<string>(), Tracks, null, random,
                playCount: path => path == neverPlayed ? 0 : 20)!;
            counts[next]++;
        }

        double averageOther = counts.Where(pair => pair.Key != neverPlayed).Average(pair => pair.Value);
        Assert.True(counts[neverPlayed] > averageOther * 5, $"never={counts[neverPlayed]}, other={averageOther}");
    }

    [Fact]
    public void Folders_PlaysAFolderInOrderThenSwitchesToAnotherFolder()
    {
        var random = new Random(3);
        var bag = new List<string>();

        Assert.Equal(Tracks[1], ShuffleModeSelector.TakeNext(ShuffleModes.Folders, bag, Tracks, Tracks[0], random));
        Assert.Equal(Tracks[2], ShuffleModeSelector.TakeNext(ShuffleModes.Folders, bag, Tracks, Tracks[1], random));

        string afterFolder = ShuffleModeSelector.TakeNext(ShuffleModes.Folders, bag, Tracks, Tracks[2], random)!;
        Assert.NotEqual(@"C:\m\a", System.IO.Path.GetDirectoryName(afterFolder));
        // Новая папка начинается с её первого трека.
        Assert.True(afterFolder == Tracks[3] || afterFolder == Tracks[5]);
    }

    [Fact]
    public void Folders_DoesNotReturnToAFolderUntilTheOthersHavePlayed()
    {
        var random = new Random(9);
        var bag = new List<string>();
        string current = Tracks[5]; // единственный трек папки c: следующий шаг — всегда переход в другую папку
        var visited = new List<string>();
        for (int step = 0; step < 40 && visited.Count < 2; step++)
        {
            current = ShuffleModeSelector.TakeNext(ShuffleModes.Folders, bag, Tracks, current, random)!;
            string folder = System.IO.Path.GetDirectoryName(current)!;
            if (visited.LastOrDefault() != folder) visited.Add(folder);
        }

        // Колода первого круга — папки a и b: каждая играет по разу, c в неё не входит, пока идёт её же круг.
        Assert.Equal(2, visited.Distinct().Count());
        Assert.DoesNotContain(@"C:\m\c", visited);
    }
}
