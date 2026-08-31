using System;
using System.Collections.Generic;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class ShuffleBagSelectorTests
{
    [Fact]
    public void TakeNext_ConsumesEveryActiveTrackBeforeRepeating()
    {
        var active = new[] { "a.mp3", "b.mp3", "c.mp3", "d.mp3" };
        var bag = new List<string>();
        var random = new Random(42);
        var selected = new List<string>();

        for (int i = 0; i < active.Length; i++)
            selected.Add(ShuffleBagSelector.TakeNext(bag, active, selected.LastOrDefault(), random)!);

        Assert.Equal(active.Length, selected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(active.Length, selected.Count);
        Assert.Empty(bag);
    }

    [Fact]
    public void TakeNext_DoesNotRepeatCurrentTrackAtBagBoundary()
    {
        var active = new[] { "a.mp3", "b.mp3", "c.mp3" };
        var bag = new List<string> { "a.mp3" };

        string? next = ShuffleBagSelector.TakeNext(bag, active, "a.mp3", new Random(1));

        Assert.NotNull(next);
        Assert.False(string.Equals("a.mp3", next, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TakeNext_RemovesInactiveTracksAndHandlesDuplicates()
    {
        var bag = new List<string> { "missing.mp3", "A.mp3", "a.mp3" };
        var active = new[] { "A.mp3", "B.mp3", "b.mp3" };

        string? next = ShuffleBagSelector.TakeNext(bag, active, "A.mp3", new Random(2));

        Assert.NotNull(next);
        Assert.NotEqual("A.mp3", next, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("missing.mp3", next, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing.mp3", bag);
    }

    [Fact]
    public void TakeNext_HandlesEmptyAndSingleTrackPlaylists()
    {
        var bag = new List<string>();

        Assert.Null(ShuffleBagSelector.TakeNext(bag, Array.Empty<string>(), null, new Random(3)));
        Assert.Equal("only.mp3", ShuffleBagSelector.TakeNext(
            bag, new[] { "only.mp3" }, "only.mp3", new Random(3)));
    }
}
