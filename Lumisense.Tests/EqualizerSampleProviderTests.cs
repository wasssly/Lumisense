using AudioPlayer;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class EqualizerSampleProviderTests
{
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
