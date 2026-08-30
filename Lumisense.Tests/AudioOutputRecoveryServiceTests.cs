using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioOutputRecoveryServiceTests
{
    [Fact]
    public void TryBegin_AcceptsRequestAndLeaseCompletesCoordinator()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.FromSeconds(1));
        var request = new OutputRecoveryRequest(
            OutputRecoveryReason.EndpointUnavailable,
            "endpoint disconnected",
            ResumePlayback: true,
            ExpectedDeviceEvent: true);

        using AudioOutputRecoveryService.OutputRecoveryLease? lease =
            service.TryBegin(request, DateTime.UtcNow, out OutputRecoveryDecision decision);

        Assert.NotNull(lease);
        Assert.True(decision.Accepted);
        Assert.True(decision.CanResume);
        Assert.Equal(1, decision.RecoveryCount);
        Assert.True(coordinator.IsInProgress);

        lease!.Dispose();
        Assert.False(coordinator.IsInProgress);
    }

    [Fact]
    public void TryBegin_RejectsSecondRequestWhileFirstLeaseIsActive()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.Zero);
        var request = CreateRequest(resumePlayback: false);
        using AudioOutputRecoveryService.OutputRecoveryLease? first =
            service.TryBegin(request, DateTime.UtcNow, out _);

        AudioOutputRecoveryService.OutputRecoveryLease? second = service.TryBegin(
            request, DateTime.UtcNow.AddSeconds(1), out OutputRecoveryDecision decision);

        Assert.Null(second);
        Assert.False(decision.Accepted);
        Assert.Equal("recovery уже выполняется", decision.RejectionReason);
    }

    [Fact]
    public void TryBegin_RejectsRequestDuringCooldownAndAcceptsAfterIt()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.FromSeconds(5));
        var request = CreateRequest(resumePlayback: false);
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        using AudioOutputRecoveryService.OutputRecoveryLease? first =
            service.TryBegin(request, started, out _);
        first!.Dispose();

        Assert.Null(service.TryBegin(request, started.AddSeconds(4), out OutputRecoveryDecision blocked));
        Assert.False(blocked.Accepted);
        Assert.Equal("recovery находится в cooldown", blocked.RejectionReason);

        using AudioOutputRecoveryService.OutputRecoveryLease? second =
            service.TryBegin(request, started.AddSeconds(5), out OutputRecoveryDecision accepted);
        Assert.NotNull(second);
        Assert.True(accepted.Accepted);
        Assert.False(accepted.CanResume);
    }

    [Fact]
    public void Execute_PassesSnapshotToRecoveryCallbackAndReleasesLease()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.Zero);
        var request = CreateRequest(resumePlayback: true);
        var snapshot = new PlaybackRecoverySnapshot(
            "track.wav", TimeSpan.FromSeconds(12), WasPlaying: true,
            "wasapi:saved", "wasapi:active");
        PlaybackRecoverySnapshot? received = null;

        OutputRecoveryExecutionResult result = service.Execute(
            request,
            DateTime.UtcNow,
            () => snapshot,
            value => received = value,
            out OutputRecoveryDecision decision);

        Assert.True(result.Started);
        Assert.True(result.Completed);
        Assert.True(decision.Accepted);
        Assert.Equal(snapshot, received);
        Assert.False(coordinator.IsInProgress);
    }

    [Fact]
    public void Execute_WithoutSnapshot_CompletesLeaseAndReportsFailure()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.Zero);

        OutputRecoveryExecutionResult result = service.Execute(
            CreateRequest(resumePlayback: false),
            DateTime.UtcNow,
            () => null,
            _ => throw new InvalidOperationException("callback should not run"),
            out OutputRecoveryDecision decision);

        Assert.True(result.Started);
        Assert.False(result.Completed);
        Assert.True(decision.Accepted);
        Assert.Contains("текущий трек", result.FailureReason);
        Assert.False(coordinator.IsInProgress);
    }

    [Fact]
    public void Execute_PausedRequest_PreservesPausedStateInSnapshot()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.Zero);
        var snapshot = new PlaybackRecoverySnapshot(
            "paused.wav", TimeSpan.FromSeconds(8), WasPlaying: false,
            "wasapi:saved", "wasapi:fallback");
        PlaybackRecoverySnapshot? received = null;

        OutputRecoveryExecutionResult result = service.Execute(
            CreateRequest(resumePlayback: false),
            DateTime.UtcNow,
            () => snapshot,
            value => received = value,
            out OutputRecoveryDecision decision);

        Assert.True(result.Completed);
        Assert.True(decision.Accepted);
        Assert.False(decision.CanResume);
        Assert.False(received!.WasPlaying);
        Assert.Equal(TimeSpan.FromSeconds(8), received.Position);
    }

    [Fact]
    public void Execute_CallbackThrows_ReleasesRecoveryLease()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => service.Execute(
            CreateRequest(resumePlayback: true),
            DateTime.UtcNow,
            () => new PlaybackRecoverySnapshot(
                "track.wav", TimeSpan.Zero, WasPlaying: true, "saved", "active"),
            _ => throw new InvalidOperationException("resume failed"),
            out _));

        Assert.False(coordinator.IsInProgress);
    }

    [Fact]
    public void Execute_SnapshotCallbackThrows_ReleasesRecoveryLease()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.Zero);

        OutputRecoveryDecision decision = default!;
        Assert.Throws<InvalidOperationException>(() => service.Execute(
            CreateRequest(resumePlayback: true),
            DateTime.UtcNow,
            () => throw new InvalidOperationException("snapshot failed"),
            _ => throw new InvalidOperationException("recovery callback must not run"),
            out decision));

        Assert.True(decision.Accepted);
        Assert.False(coordinator.IsInProgress);
    }

    [Fact]
    public void Execute_CallbackThrows_AllowsSubsequentRecovery()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.Zero);
        var request = CreateRequest(resumePlayback: true);
        var snapshot = new PlaybackRecoverySnapshot(
            "track.wav", TimeSpan.Zero, WasPlaying: true, "saved", "active");

        Assert.Throws<InvalidOperationException>(() => service.Execute(
            request,
            DateTime.UtcNow,
            () => snapshot,
            _ => throw new InvalidOperationException("recovery failed"),
            out _));

        bool executed = false;
        OutputRecoveryExecutionResult result = service.Execute(
            request,
            DateTime.UtcNow.AddSeconds(1),
            () => snapshot,
            _ => executed = true,
            out OutputRecoveryDecision decision);

        Assert.True(result.Started);
        Assert.True(result.Completed);
        Assert.True(decision.Accepted);
        Assert.True(executed);
        Assert.False(coordinator.IsInProgress);
    }

    [Fact]
    public void Execute_RejectsCooldownWithoutInvokingRecoveryCallbacks()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        var service = new AudioOutputRecoveryService(coordinator, TimeSpan.FromSeconds(5));
        var request = CreateRequest(resumePlayback: true);
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        using AudioOutputRecoveryService.OutputRecoveryLease? first =
            service.TryBegin(request, started, out _);
        first!.Dispose();
        bool captured = false;
        bool executed = false;

        OutputRecoveryExecutionResult result = service.Execute(
            request,
            started.AddSeconds(1),
            () =>
            {
                captured = true;
                return null;
            },
            _ => executed = true,
            out OutputRecoveryDecision decision);

        Assert.False(result.Started);
        Assert.False(decision.Accepted);
        Assert.False(captured);
        Assert.False(executed);
    }

    [Fact]
    public void Constructor_RejectsNegativeCooldown()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioOutputRecoveryService(new AudioOutputRecoveryCoordinator(), TimeSpan.FromMilliseconds(-1)));
    }

    private static OutputRecoveryRequest CreateRequest(bool resumePlayback) => new(
        OutputRecoveryReason.PlaybackFailure,
        "output failure",
        resumePlayback,
        ExpectedDeviceEvent: false);
}
