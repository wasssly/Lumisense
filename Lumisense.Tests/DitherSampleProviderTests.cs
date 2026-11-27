using Lumisense;
using System;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class DitherSampleProviderTests
{
    private static float[] Run(float[] input, int targetBits)
    {
        var provider = new DitherSampleProvider(new ArrayProvider(input)) { TargetBits = targetBits };
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

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(8)]
    public void UnsupportedBitDepth_PassesSamplesThrough(int bits)
    {
        float[] input = Enumerable.Range(0, 2000).Select(i => 0.3f * (float)Math.Sin(i * 0.03)).ToArray();

        Assert.Equal(input, Run(input, bits));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void NoiseStaysWithinOneLsb_AndHasNoDcOffset(int bits)
    {
        float lsb = 1f / (1 << (bits - 1));
        float[] input = Enumerable.Repeat(0.25f, 200_000).ToArray();

        float[] output = Run(input, bits);
        double mean = output.Zip(input, (o, i) => (double)(o - i)).Average();

        Assert.All(output.Zip(input, (o, i) => Math.Abs(o - i)), difference => Assert.True(difference <= lsb * 1.0001f));
        Assert.True(Math.Abs(mean) < lsb * 0.02, $"DC offset {mean}");
        Assert.NotEqual(input, output);
    }

    [Fact]
    public void DigitalSilence_StaysSilent()
    {
        float[] output = Run(new float[4800], 16);

        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Output_NeverLeavesFullScale()
    {
        float[] input = Enumerable.Repeat(1f, 5000).Concat(Enumerable.Repeat(-1f, 5000)).ToArray();

        float[] output = Run(input, 16);

        Assert.All(output, sample => Assert.InRange(sample, -1f, 1f));
    }

    private sealed class ArrayProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArrayProvider(float[] samples)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
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
