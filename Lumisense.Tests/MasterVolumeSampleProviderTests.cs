using Lumisense;
using System;
using System.Linq;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class MasterVolumeSampleProviderTests
{
    [Fact]
    public void UnityGain_LeavesSamplesUntouched()
    {
        float[] samples = Enumerable.Range(0, 480).Select(i => (float)Math.Sin(i * 0.1) * 0.5f).ToArray();
        var provider = new MasterVolumeSampleProvider(new ConstantProvider(samples, 2), 1f);

        var output = new float[samples.Length];
        provider.Read(output);

        Assert.Equal(samples, output);
    }

    [Fact]
    public void GainChange_RampsWithoutStepAndConverges()
    {
        var source = new ConstantProvider(Enumerable.Repeat(1f, 48000).ToArray(), 1);
        var provider = new MasterVolumeSampleProvider(source, 1f);
        provider.Gain = 0.25f;

        var output = new float[4800];
        provider.Read(output);

        double maxStep = 0;
        for (int i = 1; i < output.Length; i++)
            maxStep = Math.Max(maxStep, Math.Abs(output[i] - output[i - 1]));

        Assert.True(maxStep < 0.01, $"Ступенька {maxStep:F4}");
        Assert.InRange(output[^1], 0.2499f, 0.2501f);
        Assert.True(output[0] > 0.9f);
    }

    [Fact]
    public void SameGainOnAllChannelsOfFrame()
    {
        var source = new ConstantProvider(Enumerable.Repeat(1f, 960).ToArray(), 2);
        var provider = new MasterVolumeSampleProvider(source, 1f) { Gain = 0f };

        var output = new float[960];
        provider.Read(output);

        for (int frame = 0; frame < output.Length; frame += 2)
            Assert.Equal(output[frame], output[frame + 1]);
    }

    private sealed class ConstantProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ConstantProvider(float[] samples, int channels)
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
