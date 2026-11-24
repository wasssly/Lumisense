using System;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class SpringTrackTests
{
    [Fact]
    public void SpringSpec_DampingRatio_FollowsTheCssTokenFormula()
    {
        var critical = new SpringSpec(400, 40);

        Assert.Equal(1.0, critical.DampingRatio, 6);
    }

    [Fact]
    public void Step_SettlesExactlyOnTheTarget()
    {
        var spring = new SpringValue { Value = 0, Target = 1 };
        MotionTokens.TogglePosition.ApplyTo(spring);
        int applied = 0;
        var track = new SpringTrack(() => applied++, spring);

        int frames = 0;
        while (track.Step(1.0 / 60) && frames < 600) frames++;

        Assert.True(frames < 600);
        Assert.Equal(1.0, spring.Value, 6);
        Assert.True(applied > 0);
    }

    [Fact]
    public void KeepAlive_HoldsTheTrackOpenWhileStartsArePending()
    {
        var spring = new SpringValue { Value = 0, Target = 0 };
        bool pending = true;
        var track = new SpringTrack(() => { }, spring) { KeepAlive = () => pending };

        Assert.True(track.Step(1.0 / 60));

        pending = false;
        Assert.False(track.Step(1.0 / 60));
    }

    [Fact]
    public void Finish_JumpsToTargetAndReportsSettled()
    {
        var spring = new SpringValue { Value = 0, Target = 1, Velocity = 5 };
        bool settled = false;
        var track = new SpringTrack(() => { }, spring) { Settled = () => settled = true };

        track.Finish();

        Assert.Equal(1.0, spring.Value);
        Assert.Equal(0.0, spring.Velocity);
        Assert.True(settled);
    }

    [Fact]
    public void ToggleStretch_KickGrowsThenReturnsToZero()
    {
        var stretch = new SpringValue { Value = 0, Target = 0, Velocity = MotionTokens.ToggleStretchKick };
        MotionTokens.ToggleStretch.ApplyTo(stretch);

        double peak = 0;
        int frames = 0;
        while (stretch.Step(1.0 / 60) && frames < 600)
        {
            peak = Math.Max(peak, stretch.Value);
            frames++;
        }

        Assert.True(peak > 0.5);
        Assert.True(frames < 600);
        Assert.Equal(0.0, stretch.Value, 6);
    }
}
