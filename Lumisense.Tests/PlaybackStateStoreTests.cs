using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class PlaybackStateStoreTests
{
    [Fact]
    public void Publish_PausedSnapshot_NotifiesSubscribersWithPausedState()
    {
        var store = new PlaybackStateStore();
        PlaybackSnapshot? observed = null;
        store.Changed += snapshot => observed = snapshot;

        store.Publish(new PlaybackSnapshot(
            TrackPath: null,
            Title: string.Empty,
            Artist: string.Empty,
            IsPlaying: false,
            PositionSeconds: 12,
            DurationSeconds: 180));

        Assert.NotNull(observed);
        Assert.False(observed!.IsPlaying);
        Assert.Equal(12, observed.PositionSeconds);
        Assert.Equal(180, observed.DurationSeconds);
    }

    [Fact]
    public void Publish_PlayingAndPausedSnapshots_PreservesStateTransitionOrder()
    {
        var store = new PlaybackStateStore();
        var states = new List<bool>();
        store.Changed += snapshot => states.Add(snapshot.IsPlaying);

        store.Publish(new PlaybackSnapshot(null, "", "", true, 0, 180));
        store.Publish(new PlaybackSnapshot(null, "", "", false, 0, 180));

        Assert.Equal(new[] { true, false }, states);
        Assert.False(store.Current.IsPlaying);
    }
}
