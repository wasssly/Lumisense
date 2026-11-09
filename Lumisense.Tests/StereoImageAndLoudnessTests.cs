using Lumisense;
using System;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class StereoImageAndLoudnessTests
{
    private static float[] Stereo(int frames, Func<int, (float L, float R)> frame)
    {
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
            (samples[i * 2], samples[i * 2 + 1]) = frame(i);
        return samples;
    }

    [Fact]
    public void NeutralSettings_LeaveStereoUntouched()
    {
        float[] samples = Stereo(960, i => ((float)Math.Sin(i * 0.1) * 0.5f, (float)Math.Cos(i * 0.07) * 0.4f));
        var provider = new StereoImageSampleProvider(new SourceProvider(samples, 2));

        var output = new float[samples.Length];
        provider.Read(output);

        Assert.Equal(samples, output);
    }

    [Fact]
    public void ZeroWidth_ConvergesToMono()
    {
        float[] samples = Stereo(48000, i => (0.5f, -0.3f));
        var provider = new StereoImageSampleProvider(new SourceProvider(samples, 2)) { WidthPercent = 0 };

        var output = new float[samples.Length];
        provider.Read(output);

        Assert.InRange(output[^2], 0.0999f, 0.1001f);
        Assert.InRange(output[^1], 0.0999f, 0.1001f);
    }

    [Fact]
    public void Crossfeed_LeaksLowFrequenciesToTheOtherEar()
    {
        float[] samples = Stereo(48000, i => ((float)Math.Sin(2 * Math.PI * 100 * i / 48000.0) * 0.5f, 0f));
        var provider = new StereoImageSampleProvider(new SourceProvider(samples, 2)) { CrossfeedPercent = 100 };

        var output = new float[samples.Length];
        provider.Read(output);

        double rightPeak = output.Where((_, i) => i % 2 == 1).Skip(24000).Max(x => Math.Abs(x));
        Assert.True(rightPeak > 0.05, $"Правый канал {rightPeak:F3}");
    }

    [Fact]
    public void MonoSource_IsPassedThrough()
    {
        float[] samples = Enumerable.Range(0, 480).Select(i => (float)Math.Sin(i * 0.1) * 0.5f).ToArray();
        var provider = new StereoImageSampleProvider(new SourceProvider(samples, 1)) { WidthPercent = 0, CrossfeedPercent = 100 };

        var output = new float[samples.Length];
        provider.Read(output);

        Assert.Equal(samples, output);
    }

    [Theory]
    [InlineData(1.0, 100, 0)]
    [InlineData(0.5, 100, 0)]
    [InlineData(0.1, 0, 0)]
    public void Loudness_NoBoostAtLoudVolumeOrZeroStrength(double gain, int strength, double expectedDb)
    {
        var (bass, treble) = LoudnessSampleProvider.BoostFor(gain, strength);
        Assert.Equal(expectedDb, bass, 6);
        Assert.Equal(expectedDb, treble, 6);
    }

    [Fact]
    public void Loudness_BoostGrowsAsVolumeDropsAndIsCapped()
    {
        var (bassQuiet, trebleQuiet) = LoudnessSampleProvider.BoostFor(0.1, 100);
        var (bassVeryQuiet, trebleVeryQuiet) = LoudnessSampleProvider.BoostFor(0.001, 100);

        Assert.True(bassQuiet > 0 && trebleQuiet > 0 && bassQuiet > trebleQuiet);
        Assert.True(bassVeryQuiet >= bassQuiet && trebleVeryQuiet >= trebleQuiet);
        Assert.True(bassVeryQuiet <= 9 && trebleVeryQuiet <= 4);
    }

    [Fact]
    public void Loudness_RaisesBassOnlyWhenQuiet()
    {
        float[] samples = Enumerable.Range(0, 48000).Select(i => (float)Math.Sin(2 * Math.PI * 50 * i / 48000.0) * 0.1f).ToArray();

        double Peak(double gain)
        {
            var provider = new LoudnessSampleProvider(new SourceProvider(samples, 1)) { StrengthPercent = 100, VolumeGain = gain };
            var output = new float[samples.Length];
            provider.Read(output);
            return output.Skip(24000).Max(x => Math.Abs(x));
        }

        Assert.InRange(Peak(1.0), 0.0999, 0.1001);
        Assert.True(Peak(0.05) > 0.13);
    }

    private sealed class SourceProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public SourceProvider(float[] samples, int channels)
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
