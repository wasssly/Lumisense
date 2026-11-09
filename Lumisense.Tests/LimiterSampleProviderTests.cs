using Lumisense;
using System;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class LimiterSampleProviderTests
{
    private static float[] Run(float[] input, int channels, bool enabled = true)
    {
        var provider = new LimiterSampleProvider(new ArrayProvider(input, channels)) { Enabled = enabled };
        var output = new float[input.Length];
        int offset = 0;
        while (offset < output.Length)
        {
            int read = provider.Read(output.AsSpan(offset, Math.Min(480, output.Length - offset)));
            if (read == 0) break;
            offset += read;
        }

        return output;
    }

    [Fact]
    public void QuietSignal_IsOnlyDelayed()
    {
        float[] input = Enumerable.Range(0, 4800).Select(i => 0.5f * (float)Math.Sin(i * 0.05)).ToArray();
        float[] output = Run(input, 1);

        const int delay = 96;
        for (int i = delay; i < input.Length; i++)
            Assert.Equal(input[i - delay], output[i], 6);
    }

    [Fact]
    public void LoudSignal_NeverExceedsCeiling()
    {
        float[] input = Enumerable.Range(0, 48000).Select(i => 2.5f * (float)Math.Sin(i * 0.03)).ToArray();
        float[] output = Run(input, 1);

        Assert.True(output.Max(sample => Math.Abs(sample)) <= LimiterSampleProvider.Ceiling + 1e-6f);
        Assert.True(output.Max(sample => Math.Abs(sample)) > 0.9f);
    }

    [Fact]
    public void Disabled_DoesNotChangeLevel()
    {
        float[] input = Enumerable.Repeat(1.5f, 4800).ToArray();
        float[] output = Run(input, 2, enabled: false);

        Assert.Equal(1.5f, output[^1], 6);
    }

    private sealed class ArrayProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArrayProvider(float[] samples, int channels)
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
