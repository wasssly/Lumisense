namespace AudioPlayer;

/// <summary>
/// Owns the user-visible playback state and rejects impossible state transitions.
/// </summary>
internal sealed class PlaybackStateMachine
{
    private static readonly IReadOnlyDictionary<TrackUserState, IReadOnlySet<TrackUserState>> AllowedTransitions =
        new Dictionary<TrackUserState, IReadOnlySet<TrackUserState>>
        {
            [TrackUserState.NoTrack] = States(TrackUserState.NoTrack, TrackUserState.Loading, TrackUserState.Stopped, TrackUserState.Error),
            [TrackUserState.Loading] = States(TrackUserState.Loading, TrackUserState.Playing, TrackUserState.Paused, TrackUserState.Stopped, TrackUserState.Error, TrackUserState.NoTrack),
            [TrackUserState.Playing] = States(TrackUserState.Playing, TrackUserState.Paused, TrackUserState.Loading, TrackUserState.Stopped, TrackUserState.Error, TrackUserState.NoTrack),
            [TrackUserState.Paused] = States(TrackUserState.Paused, TrackUserState.Playing, TrackUserState.Loading, TrackUserState.Stopped, TrackUserState.Error, TrackUserState.NoTrack),
            [TrackUserState.Stopped] = States(TrackUserState.Stopped, TrackUserState.Loading, TrackUserState.Playing, TrackUserState.Paused, TrackUserState.Error, TrackUserState.NoTrack),
            [TrackUserState.Error] = States(TrackUserState.Error, TrackUserState.Loading, TrackUserState.Stopped, TrackUserState.NoTrack)
        };

    public PlaybackStateMachine(TrackUserState initialState = TrackUserState.NoTrack)
    {
        Current = initialState;
    }

    public TrackUserState Current { get; private set; }

    public event Action<TrackUserState, TrackUserState>? Transitioned;

    public bool TryTransitionTo(TrackUserState nextState)
    {
        if (Current == nextState)
            return true;

        if (!AllowedTransitions[Current].Contains(nextState))
            return false;

        TrackUserState previousState = Current;
        Current = nextState;
        Transitioned?.Invoke(previousState, nextState);
        return true;
    }

    public void TransitionTo(TrackUserState nextState)
    {
        if (!TryTransitionTo(nextState))
        {
            throw new InvalidOperationException(
                $"Недопустимый переход состояния воспроизведения: {Current} → {nextState}.");
        }
    }

    private static IReadOnlySet<TrackUserState> States(params TrackUserState[] states) =>
        new HashSet<TrackUserState>(states);
}
