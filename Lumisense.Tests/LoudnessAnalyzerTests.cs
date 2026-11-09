using Lumisense;
using System;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class LoudnessAnalyzerTests
{
    private static float[] Sine(double frequency, double amplitude, int seconds, int channels, int sampleRate = 48000)
    {
        var samples = new float[seconds * sampleRate * channels];
        for (int i = 0; i < seconds * sampleRate; i++)
        {
            float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            for (int c = 0; c < channels; c++) samples[i * channels + c] = value;
        }

        return samples;
    }

    [Fact]
    public void FullScaleMonoSine_IsAboutMinus3Lufs()
    {
        double lufs = LoudnessAnalyzer.MeasureIntegratedLufs(new Source(Sine(997, 1.0, 5, 1), 1));
        Assert.InRange(lufs, -3.3, -2.7);
    }

    [Fact]
    public void HalvingTheAmplitudeLowersLoudnessByAboutSixDecibels()
    {
        double loud = LoudnessAnalyzer.MeasureIntegratedLufs(new Source(Sine(997, 0.5, 5, 2), 2));
        double quiet = LoudnessAnalyzer.MeasureIntegratedLufs(new Source(Sine(997, 0.25, 5, 2), 2));
        Assert.InRange(loud - quiet, 5.9, 6.1);
    }

    [Fact]
    public void Silence_IsNegativeInfinity()
    {
        double lufs = LoudnessAnalyzer.MeasureIntegratedLufs(new Source(new float[48000 * 2 * 3], 2));
        Assert.True(double.IsNegativeInfinity(lufs));
    }

    [Fact]
    public void RelativeGate_IgnoresLongQuietPassages()
    {
        var loud = Sine(997, 0.5, 4, 2);
        var quiet = Sine(997, 0.005, 20, 2);
        double mixed = LoudnessAnalyzer.MeasureIntegratedLufs(new Source(loud.Concat(quiet).ToArray(), 2));
        double onlyLoud = LoudnessAnalyzer.MeasureIntegratedLufs(new Source(loud, 2));
        Assert.InRange(Math.Abs(mixed - onlyLoud), 0, 0.5);
    }

    [Theory]
    [InlineData(-18, 0, 0)]
    [InlineData(-28, 0, 9)]
    [InlineData(-8, 0, -10)]
    [InlineData(-50, 0, 9)]
    [InlineData(-18, 3, 3)]
    public void GainLinearFor_MapsToReferenceWithLimits(double lufs, double preamp, double expectedDb)
    {
        double gain = LoudnessAnalyzer.GainLinearFor(lufs, preamp);
        Assert.Equal(expectedDb, 20 * Math.Log10(gain), 3);
    }

    [Fact]
    public void GainLinearFor_Infinity_IsNeutral() => Assert.Equal(1.0, LoudnessAnalyzer.GainLinearFor(double.NegativeInfinity, 0));

    private sealed class Source : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public Source(float[] samples, int channels)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
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
