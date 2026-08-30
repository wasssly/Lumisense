using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioOutputRecoveryCoordinatorTests
{
    [Fact]
    public void TryBegin_RejectsReentryUntilRecoveryCompletes()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(coordinator.TryBegin(started, TimeSpan.FromSeconds(1)));
        Assert.False(coordinator.TryBegin(started.AddSeconds(2), TimeSpan.FromSeconds(1)));
        Assert.True(coordinator.IsInProgress);
        Assert.Equal(1, coordinator.RecoveryCount);
    }

    [Fact]
    public void TryBegin_RejectsRecoveryDuringCooldownAfterCompletion()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan cooldown = TimeSpan.FromSeconds(5);

        Assert.True(coordinator.TryBegin(started, cooldown));
        coordinator.Complete();

        Assert.False(coordinator.IsInProgress);
        Assert.False(coordinator.TryBegin(started.AddSeconds(4), cooldown));
        Assert.Equal(1, coordinator.RecoveryCount);
    }

    [Fact]
    public void TryBegin_AllowsRecoveryAfterCooldown()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan cooldown = TimeSpan.FromSeconds(5);

        Assert.True(coordinator.TryBegin(started, cooldown));
        coordinator.Complete();

        Assert.True(coordinator.TryBegin(started.AddSeconds(5), cooldown));
        Assert.Equal(2, coordinator.RecoveryCount);
    }

    [Fact]
    public void Complete_IsIdempotentAndPreservesLastStart()
    {
        var coordinator = new AudioOutputRecoveryCoordinator();
        DateTime started = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(coordinator.TryBegin(started, TimeSpan.Zero));
        coordinator.Complete();
        coordinator.Complete();

        Assert.False(coordinator.IsInProgress);
        Assert.Equal(started, coordinator.LastStartedUtc);
        Assert.Equal(1, coordinator.RecoveryCount);
    }
}
