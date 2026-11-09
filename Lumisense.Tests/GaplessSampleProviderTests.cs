using Lumisense;
using System;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class GaplessSampleProviderTests
{
    [Fact]
    public void SwitchesToArmedSourceWithinTheSameRead()
    {
        var provider = new GaplessSampleProvider(new Source(Enumerable.Repeat(1f, 100).ToArray()));
        object? switched = null;
        int switchCount = 0;
        provider.Switched += token => { switched = token; switchCount++; };
        Assert.True(provider.Arm(new Source(Enumerable.Repeat(2f, 100).ToArray()), "next"));

        var buffer = new float[150];
        int read = provider.Read(buffer);

        Assert.Equal(150, read);
        Assert.Equal(1f, buffer[99]);
        Assert.Equal(2f, buffer[100]);
        Assert.Equal("next", switched);
        Assert.Equal(1, switchCount);
    }

    [Fact]
    public void WithoutArmedSource_ReturnsRemainderThenZero()
    {
        var provider = new GaplessSampleProvider(new Source(Enumerable.Repeat(1f, 100).ToArray()));

        var buffer = new float[150];
        Assert.Equal(100, provider.Read(buffer));
        Assert.Equal(0, provider.Read(buffer));
    }

    [Fact]
    public void ArmWithDifferentFormat_IsRejected()
    {
        var provider = new GaplessSampleProvider(new Source(new float[10], sampleRate: 48000));

        Assert.False(provider.Arm(new Source(new float[10], sampleRate: 44100), "next"));
        Assert.False(provider.Arm(new Source(new float[10], channels: 1), "next"));
    }

    [Fact]
    public void SecondArmIsRejectedUntilDisarmed()
    {
        var provider = new GaplessSampleProvider(new Source(new float[10]));

        Assert.True(provider.Arm(new Source(new float[10]), "first"));
        Assert.False(provider.Arm(new Source(new float[10]), "second"));
        Assert.Equal("first", provider.Disarm());
        Assert.True(provider.Arm(new Source(new float[10]), "second"));
    }

    [Fact]
    public void Disarm_PreventsSwitching()
    {
        var provider = new GaplessSampleProvider(new Source(Enumerable.Repeat(1f, 20).ToArray()));
        bool switched = false;
        provider.Switched += _ => switched = true;
        provider.Arm(new Source(Enumerable.Repeat(2f, 20).ToArray()), "next");
        Assert.NotNull(provider.Disarm());
        Assert.Null(provider.Disarm());

        var buffer = new float[40];
        Assert.Equal(20, provider.Read(buffer));
        Assert.False(switched);
    }

    [Fact]
    public void DisarmAfterSwitch_ReturnsNull()
    {
        var provider = new GaplessSampleProvider(new Source(new float[4]));
        provider.Arm(new Source(new float[4]), "next");

        provider.Read(new float[8]);

        Assert.Null(provider.Disarm());
    }

    private sealed class Source : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public Source(float[] samples, int sampleRate = 48000, int channels = 2)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, _samples.Length - _position);
            _samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }
}
