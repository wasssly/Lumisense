using System;
using System.Collections.Generic;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

[Collection(StaticManagersCollection.Name)]
public sealed class PlayCountManagerTests : IDisposable
{
    public PlayCountManagerTests() => PlayCountManager.Reset();

    public void Dispose() => PlayCountManager.Reset();

    [Fact]
    public void Initialize_KeepsOnlyPositiveCountsForNonBlankPaths()
    {
        PlayCountManager.Initialize(new Dictionary<string, int>
        {
            ["a.mp3"] = 3,
            ["zero.mp3"] = 0,
            ["negative.mp3"] = -2,
            [" "] = 5,
            [""] = 7
        });

        Assert.Equal(3, PlayCountManager.GetCount("a.mp3"));
        Assert.Equal(0, PlayCountManager.GetCount("zero.mp3"));
        Assert.Equal(0, PlayCountManager.GetCount("negative.mp3"));
        Assert.Single(PlayCountManager.GetAll());
    }

    [Fact]
    public void Initialize_ReplacesPreviousCounts()
    {
        PlayCountManager.Initialize(new Dictionary<string, int> { ["old.mp3"] = 9 });

        PlayCountManager.Initialize(new Dictionary<string, int> { ["new.mp3"] = 1 });

        Assert.Equal(0, PlayCountManager.GetCount("old.mp3"));
        Assert.Equal(1, PlayCountManager.GetCount("new.mp3"));
    }

    [Fact]
    public void GetCount_ReturnsZeroForUnknownTrack()
    {
        Assert.Equal(0, PlayCountManager.GetCount("unknown.mp3"));
    }

    [Fact]
    public void Increment_CountsFromZeroAndAccumulates()
    {
        PlayCountManager.Increment("a.mp3");
        PlayCountManager.Increment("a.mp3");
        PlayCountManager.Increment("b.mp3");

        Assert.Equal(2, PlayCountManager.GetCount("a.mp3"));
        Assert.Equal(1, PlayCountManager.GetCount("b.mp3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Increment_IgnoresBlankPathsWithoutNotifying(string path)
    {
        int epoch = PlayCountChangeNotifier.Instance.Epoch;

        PlayCountManager.Increment(path);

        Assert.Empty(PlayCountManager.GetAll());
        Assert.Equal(epoch, PlayCountChangeNotifier.Instance.Epoch);
    }

    [Fact]
    public void GetAll_ReturnsACopy()
    {
        PlayCountManager.Increment("a.mp3");

        PlayCountManager.GetAll()["a.mp3"] = 100;
        PlayCountManager.GetAll().Clear();

        Assert.Equal(1, PlayCountManager.GetCount("a.mp3"));
    }

    [Fact]
    public void Reset_ClearsCounts()
    {
        PlayCountManager.Increment("a.mp3");

        PlayCountManager.Reset();

        Assert.Equal(0, PlayCountManager.GetCount("a.mp3"));
        Assert.Empty(PlayCountManager.GetAll());
    }

    [Fact]
    public void ChangeNotifier_BumpsOnIncrementAndReset()
    {
        int epoch = PlayCountChangeNotifier.Instance.Epoch;

        PlayCountManager.Increment("a.mp3");
        Assert.Equal(epoch + 1, PlayCountChangeNotifier.Instance.Epoch);

        PlayCountManager.Reset();
        Assert.Equal(epoch + 2, PlayCountChangeNotifier.Instance.Epoch);
    }
}
