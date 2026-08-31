using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class PlaybackQueueTests
{
    [Fact]
    public void NewQueue_IsEmpty()
    {
        var queue = new PlaybackQueue();

        Assert.Equal(0, queue.Count);
        Assert.Null(queue.PeekNext());
    }

    [Fact]
    public void PlayNext_InsertsAtFront_PreservingGivenOrder()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "existing.mp3" });

        queue.PlayNext(new[] { "a.mp3", "b.mp3" });

        Assert.Equal(new[] { "a.mp3", "b.mp3", "existing.mp3" }, queue.Items);
    }

    [Fact]
    public void AddToEnd_AppendsInGivenOrder()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3" });

        queue.AddToEnd(new[] { "b.mp3", "c.mp3" });

        Assert.Equal(new[] { "a.mp3", "b.mp3", "c.mp3" }, queue.Items);
    }

    [Fact]
    public void PlayNext_WithEmptyList_DoesNotRaiseChanged()
    {
        var queue = new PlaybackQueue();
        int changedCount = 0;
        queue.Changed += () => changedCount++;

        queue.PlayNext(Array.Empty<string>());

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void PeekNext_DoesNotRemoveItem()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3" });

        string? peeked = queue.PeekNext();

        Assert.Equal("a.mp3", peeked);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void PopNext_RemovesAndReturnsFirstItem()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3", "b.mp3" });

        string? popped = queue.PopNext();

        Assert.Equal("a.mp3", popped);
        Assert.Equal(new[] { "b.mp3" }, queue.Items);
    }

    [Fact]
    public void PopNext_OnEmptyQueue_ReturnsNull()
    {
        var queue = new PlaybackQueue();

        Assert.Null(queue.PopNext());
    }

    [Fact]
    public void Remove_ByPath_RemovesMatchingItemCaseInsensitively()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3", "b.mp3" });

        bool removed = queue.Remove("B.MP3");

        Assert.True(removed);
        Assert.Equal(new[] { "a.mp3" }, queue.Items);
    }

    [Fact]
    public void Remove_UnknownPath_ReturnsFalseAndDoesNotChangeQueue()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3" });

        bool removed = queue.Remove("missing.mp3");

        Assert.False(removed);
        Assert.Equal(new[] { "a.mp3" }, queue.Items);
    }

    [Fact]
    public void RemoveAt_ValidIndex_RemovesItem()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3", "b.mp3" });

        queue.RemoveAt(0);

        Assert.Equal(new[] { "b.mp3" }, queue.Items);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void RemoveAt_OutOfRangeIndex_IsNoOp(int index)
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3" });

        queue.RemoveAt(index);

        Assert.Equal(new[] { "a.mp3" }, queue.Items);
    }

    [Fact]
    public void Clear_EmptiesQueue()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "a.mp3", "b.mp3" });

        queue.Clear();

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Clear_OnEmptyQueue_DoesNotRaiseChanged()
    {
        var queue = new PlaybackQueue();
        int changedCount = 0;
        queue.Changed += () => changedCount++;

        queue.Clear();

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void PruneMissing_RemovesFilesThatDoNotExistOnDisk()
    {
        string existingFile = Path.GetTempFileName();
        try
        {
            var queue = new PlaybackQueue();
            queue.AddToEnd(new[] { existingFile, "C:\\definitely\\missing\\track.mp3" });

            int removed = queue.PruneMissing();

            Assert.Equal(1, removed);
            Assert.Equal(new[] { existingFile }, queue.Items);
        }
        finally
        {
            File.Delete(existingFile);
        }
    }

    [Fact]
    public void SortByDisplayName_ReordersFuturePlaybackAlphabetically()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "C.mp3", "a.mp3", "B.mp3" });

        queue.SortByDisplayName(descending: false);

        Assert.Equal(new[] { "a.mp3", "B.mp3", "C.mp3" }, queue.Items);
    }

    [Fact]
    public void RestoreInsertionOrder_ReturnsOrderBeforeSorting()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "C.mp3", "a.mp3", "B.mp3" });
        queue.SortByDisplayName(descending: false);

        queue.RestoreInsertionOrder();

        Assert.Equal(new[] { "C.mp3", "a.mp3", "B.mp3" }, queue.Items);
    }

    [Fact]
    public void LoadFrom_ReplacesQueueContents()
    {
        var queue = new PlaybackQueue();
        queue.AddToEnd(new[] { "old.mp3" });

        queue.LoadFrom(new[] { "a.mp3", "b.mp3" });

        Assert.Equal(new[] { "a.mp3", "b.mp3" }, queue.Items);
    }
}
