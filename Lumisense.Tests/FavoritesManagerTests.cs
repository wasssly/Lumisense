using System;
using System.Collections.Generic;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

[Collection(StaticManagersCollection.Name)]
public sealed class FavoritesManagerTests : IDisposable
{
    public FavoritesManagerTests() => FavoritesManager.Reset();

    public void Dispose() => FavoritesManager.Reset();

    [Fact]
    public void Initialize_SkipsEmptyPathsAndDuplicatesKeepingFirstOrder()
    {
        FavoritesManager.Initialize(new[] { "b.mp3", "", "a.mp3", "b.mp3", "c.mp3" });

        Assert.Equal(3, FavoritesManager.Count);
        Assert.Equal(new[] { "b.mp3", "a.mp3", "c.mp3" }, FavoritesManager.GetOrder());
        Assert.True(FavoritesManager.IsFavorite("a.mp3"));
        Assert.False(FavoritesManager.IsFavorite(""));
    }

    [Fact]
    public void Initialize_PinsOnlyTracksThatAreFavorites()
    {
        FavoritesManager.Initialize(new[] { "a.mp3", "b.mp3" }, new[] { "b.mp3", "ghost.mp3" });

        Assert.Equal(new[] { "b.mp3" }, FavoritesManager.GetPinnedPaths());
        Assert.True(FavoritesManager.IsPinned("b.mp3"));
        Assert.False(FavoritesManager.IsPinned("ghost.mp3"));
    }

    [Fact]
    public void Initialize_ReplacesPreviousState()
    {
        FavoritesManager.Initialize(new[] { "old.mp3" }, new[] { "old.mp3" });

        FavoritesManager.Initialize(new[] { "new.mp3" });

        Assert.False(FavoritesManager.IsFavorite("old.mp3"));
        Assert.False(FavoritesManager.IsPinned("old.mp3"));
        Assert.Equal(new[] { "new.mp3" }, FavoritesManager.GetOrder());
    }

    [Fact]
    public void SetFavorite_AddsOnceAndRemoves()
    {
        FavoritesManager.SetFavorite("a.mp3", true);
        FavoritesManager.SetFavorite("a.mp3", true);
        Assert.Equal(1, FavoritesManager.Count);

        FavoritesManager.SetFavorite("a.mp3", false);
        Assert.Equal(0, FavoritesManager.Count);
        Assert.False(FavoritesManager.IsFavorite("a.mp3"));
    }

    [Fact]
    public void SetFavorite_RemovingAPinnedTrackAlsoUnpinsIt()
    {
        FavoritesManager.Initialize(new[] { "a.mp3" }, new[] { "a.mp3" });

        FavoritesManager.SetFavorite("a.mp3", false);
        FavoritesManager.SetFavorite("a.mp3", true);

        Assert.True(FavoritesManager.IsFavorite("a.mp3"));
        Assert.False(FavoritesManager.IsPinned("a.mp3"));
    }

    [Fact]
    public void Toggle_ReturnsTheNewState()
    {
        Assert.True(FavoritesManager.Toggle("a.mp3"));
        Assert.True(FavoritesManager.IsFavorite("a.mp3"));
        Assert.False(FavoritesManager.Toggle("a.mp3"));
        Assert.False(FavoritesManager.IsFavorite("a.mp3"));
    }

    [Fact]
    public void TogglePin_WorksOnlyForFavorites()
    {
        Assert.False(FavoritesManager.TogglePin("ghost.mp3"));
        Assert.False(FavoritesManager.IsPinned("ghost.mp3"));

        FavoritesManager.SetFavorite("a.mp3", true);
        Assert.True(FavoritesManager.TogglePin("a.mp3"));
        Assert.True(FavoritesManager.IsPinned("a.mp3"));
        Assert.False(FavoritesManager.TogglePin("a.mp3"));
        Assert.False(FavoritesManager.IsPinned("a.mp3"));
    }

    [Fact]
    public void GetAll_PutsPinnedFirstAndKeepsTheRestInOrder()
    {
        FavoritesManager.Initialize(new[] { "a.mp3", "b.mp3", "c.mp3", "d.mp3" }, new[] { "c.mp3", "b.mp3" });

        Assert.Equal(new[] { "b.mp3", "c.mp3", "a.mp3", "d.mp3" }, FavoritesManager.GetAll());
        Assert.Equal(new[] { "a.mp3", "b.mp3", "c.mp3", "d.mp3" }, FavoritesManager.GetOrder());
    }

    [Fact]
    public void ReturnedCollectionsAreCopies()
    {
        FavoritesManager.Initialize(new[] { "a.mp3" }, new[] { "a.mp3" });

        FavoritesManager.GetOrder().Add("x.mp3");
        FavoritesManager.GetPinnedPaths().Clear();
        FavoritesManager.GetAll().Clear();

        Assert.Equal(new[] { "a.mp3" }, FavoritesManager.GetOrder());
        Assert.Equal(new[] { "a.mp3" }, FavoritesManager.GetPinnedPaths());
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        FavoritesManager.Initialize(new[] { "a.mp3" }, new[] { "a.mp3" });

        FavoritesManager.Reset();

        Assert.Equal(0, FavoritesManager.Count);
        Assert.Empty(FavoritesManager.GetPinnedPaths());
        Assert.False(FavoritesManager.IsFavorite("a.mp3"));
    }

    [Fact]
    public void ChangeNotifier_BumpsOnlyWhenSomethingActuallyChanged()
    {
        int epoch = FavoritesChangeNotifier.Instance.Epoch;

        FavoritesManager.SetFavorite("a.mp3", true);
        Assert.Equal(epoch + 1, FavoritesChangeNotifier.Instance.Epoch);

        FavoritesManager.SetFavorite("a.mp3", true);
        FavoritesManager.SetFavorite("missing.mp3", false);
        FavoritesManager.TogglePin("missing.mp3");
        Assert.Equal(epoch + 1, FavoritesChangeNotifier.Instance.Epoch);

        FavoritesManager.TogglePin("a.mp3");
        Assert.Equal(epoch + 2, FavoritesChangeNotifier.Instance.Epoch);

        FavoritesManager.Reset();
        Assert.Equal(epoch + 3, FavoritesChangeNotifier.Instance.Epoch);
    }

    [Fact]
    public void ChangeNotifier_RaisesPropertyChangedForEpoch()
    {
        var raised = new List<string?>();
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => raised.Add(e.PropertyName);
        FavoritesChangeNotifier.Instance.PropertyChanged += Handler;
        try
        {
            FavoritesManager.SetFavorite("a.mp3", true);
        }
        finally
        {
            FavoritesChangeNotifier.Instance.PropertyChanged -= Handler;
        }

        Assert.Equal(new[] { nameof(FavoritesChangeNotifier.Epoch) }, raised);
    }
}
