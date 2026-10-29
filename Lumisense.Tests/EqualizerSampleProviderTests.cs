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
    public void DisabledEqualizer_PassesSamplesThroughUnchanged()
    {
        float[] samples = CreateTone(100, 0.4f, 48000, 20000);
        var provider = new EqualizerSampleProvider(new TestSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), samples));
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            provider.SetBandGain(band, 6);

        var output = new float[samples.Length];
        int read = provider.Read(output);

        Assert.Equal(samples.Length, read);
        Assert.Equal(samples, output);
    }

    [Fact]
    public void GainChangedWhileDisabled_IsAppliedAfterEnabling()
    {
        float[] tone = CreateTone(1000, 0.2f, 48000, 48000);
        var provider = new EqualizerSampleProvider(new TestSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), tone));
        provider.Read(new float[4800]);

        provider.SetBandGain(5, 12.0);
        provider.Enabled = true;
        var output = new float[4800];
        provider.Read(output);
        provider.Read(output);

        double rms = Math.Sqrt(output.Sum(sample => (double)sample * sample) / output.Length);
        double ratio = rms / (0.2 / Math.Sqrt(2));
        Assert.InRange(ratio, 3.4, 4.4);
    }

    [Fact]
    public void ReEnablingAfterLongBypass_DoesNotClick()
    {
        float[] tone = CreateTone(100, 0.4f, 48000, 48000 * 3);
        var provider = new EqualizerSampleProvider(new TestSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), tone));
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            provider.SetBandGain(band, band % 2 == 0 ? 9 : -9);

        provider.Enabled = true;
        provider.Read(new float[9600]);
        provider.Enabled = false;
        provider.Read(new float[9600]);
        provider.Read(new float[9600]);
        provider.Enabled = true;
        var after = new float[4800];
        provider.Read(after);

        double maxJump = 0;
        for (int index = 1; index < after.Length; index++)
            maxJump = Math.Max(maxJump, Math.Abs(after[index] - after[index - 1]));

        Assert.All(after, sample => Assert.True(float.IsFinite(sample)));
        Assert.True(maxJump < 0.12, $"Скачок между соседними сэмплами {maxJump:F4}");
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

    private static float[] CreateTone(double frequency, float amplitude, int sampleRate, int count)
    {
        var samples = new float[count];
        for (int index = 0; index < count; index++)
            samples[index] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * index / sampleRate);
        return samples;
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
