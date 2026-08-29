using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class PlaybackStateMachineTests
{
    [Fact]
    public void TryTransitionTo_LoadingPlayingPausedAndStopped_TracksValidFlow()
    {
        var machine = new PlaybackStateMachine();
        var transitions = new List<(TrackUserState From, TrackUserState To)>();
        machine.Transitioned += (from, to) => transitions.Add((from, to));

        Assert.True(machine.TryTransitionTo(TrackUserState.Loading));
        Assert.True(machine.TryTransitionTo(TrackUserState.Playing));
        Assert.True(machine.TryTransitionTo(TrackUserState.Paused));
        Assert.True(machine.TryTransitionTo(TrackUserState.Playing));
        Assert.True(machine.TryTransitionTo(TrackUserState.Stopped));

        Assert.Equal(TrackUserState.Stopped, machine.Current);
        Assert.Equal(
            new[]
            {
                (TrackUserState.NoTrack, TrackUserState.Loading),
                (TrackUserState.Loading, TrackUserState.Playing),
                (TrackUserState.Playing, TrackUserState.Paused),
                (TrackUserState.Paused, TrackUserState.Playing),
                (TrackUserState.Playing, TrackUserState.Stopped)
            },
            transitions);
    }

    [Fact]
    public void TryTransitionTo_RepeatingCurrentState_IsSuccessfulWithoutEvent()
    {
        var machine = new PlaybackStateMachine(TrackUserState.Paused);
        int transitionCount = 0;
        machine.Transitioned += (_, _) => transitionCount++;

        Assert.True(machine.TryTransitionTo(TrackUserState.Paused));

        Assert.Equal(TrackUserState.Paused, machine.Current);
        Assert.Equal(0, transitionCount);
    }

    [Fact]
    public void TryTransitionTo_InvalidTransition_IsRejectedAndStateIsPreserved()
    {
        var machine = new PlaybackStateMachine(TrackUserState.NoTrack);

        Assert.False(machine.TryTransitionTo(TrackUserState.Playing));
        Assert.Equal(TrackUserState.NoTrack, machine.Current);
    }

    [Fact]
    public void TransitionTo_InvalidTransition_ThrowsWithoutChangingState()
    {
        var machine = new PlaybackStateMachine(TrackUserState.Error);

        Assert.Throws<InvalidOperationException>(() =>
            machine.TransitionTo(TrackUserState.Playing));
        Assert.Equal(TrackUserState.Error, machine.Current);
    }
}
