using System;

namespace AudioPlayer;

internal enum OutputRecoveryReason
{
    PlaybackFailure,
    EndpointUnavailable,
    DefaultDeviceChanged,
    OutputInitializationFailure
}

internal sealed record PlaybackRecoverySnapshot(
    string TrackPath,
    TimeSpan Position,
    bool WasPlaying,
    string? SavedDeviceKey,
    string? ActiveEndpointId);

internal sealed record OutputRecoveryRequest(
    OutputRecoveryReason Reason,
    string ErrorMessage,
    bool ResumePlayback,
    bool ExpectedDeviceEvent);

internal sealed record OutputRecoveryDecision(
    bool Accepted,
    bool CanResume,
    int RecoveryCount,
    string? RejectionReason);

internal sealed record OutputRecoveryExecutionResult(
    bool Started,
    bool Completed,
    int RecoveryCount,
    string? FailureReason);

/// <summary>
/// Coordinates recovery admission and executes the workflow supplied by the host.
/// The service is independent of WPF and does not touch WASAPI directly.
/// </summary>
internal sealed class AudioOutputRecoveryService
{
    private readonly AudioOutputRecoveryCoordinator _coordinator;
    private readonly TimeSpan _cooldown;

    public AudioOutputRecoveryService(
        AudioOutputRecoveryCoordinator coordinator,
        TimeSpan cooldown)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        if (cooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        _cooldown = cooldown;
    }

    public OutputRecoveryLease? TryBegin(
        OutputRecoveryRequest request,
        DateTime nowUtc,
        out OutputRecoveryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_coordinator.IsInProgress)
        {
            decision = new OutputRecoveryDecision(false, false, _coordinator.RecoveryCount,
                "recovery уже выполняется");
            return null;
        }

        if (!_coordinator.TryBegin(nowUtc, _cooldown))
        {
            decision = new OutputRecoveryDecision(false, false, _coordinator.RecoveryCount,
                "recovery находится в cooldown");
            return null;
        }

        decision = new OutputRecoveryDecision(true, request.ResumePlayback,
            _coordinator.RecoveryCount, null);
        return new OutputRecoveryLease(_coordinator);
    }

    public OutputRecoveryExecutionResult Execute(
        OutputRecoveryRequest request,
        DateTime nowUtc,
        Func<PlaybackRecoverySnapshot?> captureSnapshot,
        Action<PlaybackRecoverySnapshot> executeRecovery,
        out OutputRecoveryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(captureSnapshot);
        ArgumentNullException.ThrowIfNull(executeRecovery);

        OutputRecoveryLease? lease = TryBegin(request, nowUtc, out decision);
        if (lease is null)
            return new OutputRecoveryExecutionResult(false, false, decision.RecoveryCount, decision.RejectionReason);

        using (lease)
        {
            PlaybackRecoverySnapshot? snapshot = captureSnapshot();
            if (snapshot is null)
                return new OutputRecoveryExecutionResult(true, false, decision.RecoveryCount,
                    "текущий трек недоступен для восстановления");

            executeRecovery(snapshot);
            return new OutputRecoveryExecutionResult(true, true, decision.RecoveryCount, null);
        }
    }

    internal sealed class OutputRecoveryLease : IDisposable
    {
        private AudioOutputRecoveryCoordinator? _coordinator;

        internal OutputRecoveryLease(AudioOutputRecoveryCoordinator coordinator) =>
            _coordinator = coordinator;

        public void Dispose()
        {
            AudioOutputRecoveryCoordinator? coordinator =
                Interlocked.Exchange(ref _coordinator, null);
            coordinator?.Complete();
        }
    }
}
