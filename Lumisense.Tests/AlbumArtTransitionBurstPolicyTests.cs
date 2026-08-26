using System;
using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AlbumArtTransitionBurstPolicyTests
{
    [Fact]
    public void ShouldSkipAnimation_OnlyForRequestsInsideBurstWindow()
    {
        var policy = new AlbumArtTransitionBurstPolicy(TimeSpan.FromMilliseconds(240));
        DateTime start = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(policy.ShouldSkipAnimation(start));
        Assert.True(policy.ShouldSkipAnimation(start.AddMilliseconds(140)));
        Assert.True(policy.ShouldSkipAnimation(start.AddMilliseconds(280)));
        Assert.False(policy.ShouldSkipAnimation(start.AddMilliseconds(600)));
    }

    [Fact]
    public void Reset_MakesNextRequestEligibleForNormalAnimation()
    {
        var policy = new AlbumArtTransitionBurstPolicy(TimeSpan.FromMilliseconds(240));
        DateTime start = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(policy.ShouldSkipAnimation(start));
        Assert.True(policy.ShouldSkipAnimation(start.AddMilliseconds(100)));

        policy.Reset();

        Assert.False(policy.ShouldSkipAnimation(start.AddMilliseconds(120)));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveBurstWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlbumArtTransitionBurstPolicy(TimeSpan.Zero));
    }
}
