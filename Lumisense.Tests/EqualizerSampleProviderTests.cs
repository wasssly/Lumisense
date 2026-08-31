using Lumisense;
using System;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class EqualizerSampleProviderTests
{
    [Fact]
    public void BypassTransition_IsFiniteAndConvergesToDrySignal()
    {
        var source = new TestSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 1),
            Enumerable.Repeat(0.25f, 4096).ToArray());
        var provider = new EqualizerSampleProvider(source) { Enabled = true };
        provider.SetBandGain(4, 12.0);

        var warmup = new float[2048];
        provider.Read(warmup);
        Assert.All(warmup, sample => Assert.True(float.IsFinite(sample)));

        provider.Enabled = false;
        var output = new float[2048];
        provider.Read(output);

        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
        Assert.InRange(output[^1], 0.249f, 0.251f);
    }

    [Fact]
    public void LowSampleRateSource_SkipsBandAtNyquistWithoutBreakingPlayback()
    {
        var source = new TestSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(32000, 2),
            new float[] { 0.25f, -0.25f, 0.5f, -0.5f });
        var provider = new EqualizerSampleProvider(source) { Enabled = true };

        // 16 kHz равны Nyquist для 32-kHz источника. Настройка сохраняется для
        // следующего совместимого трека, но текущий EQ не должен создавать invalid filter.
        provider.SetBandGain(9, 6.0);
        var output = new float[4];
        int samplesRead = provider.Read(output);

        Assert.Equal(4, samplesRead);
        Assert.Equal(6.0, provider.GetBandGain(9), 6);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
    }

    [Theory]
    [InlineData(22050)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void StandardAudioSampleRates_ApplyAllStoredBandGainsWithoutInvalidFilters(int sampleRate)
    {
        var source = new TestSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2),
            new float[] { 0.25f, -0.25f, 0.5f, -0.5f });
        var provider = new EqualizerSampleProvider(source) { Enabled = true };

        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            provider.SetBandGain(band, band - 4.5);

        var output = new float[4];
        int samplesRead = provider.Read(output);

        Assert.Equal(4, samplesRead);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
        Assert.Equal(4.5, provider.GetBandGain(9), 6);
    }

    private sealed class TestSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public TestSampleProvider(WaveFormat waveFormat, float[] samples)
        {
            WaveFormat = waveFormat;
            _samples = samples;
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
