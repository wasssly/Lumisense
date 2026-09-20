using System.Collections.Generic;
using Lumisense;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class SoundTouchSampleProviderTests
{
    [Fact]
    public void Properties_PersistTempoAndPitchChanges()
    {
        var provider = new SoundTouchSampleProvider(new TestSampleProvider(CreateSamples(4096), channels: 2));

        provider.Tempo = 1.35;
        provider.PitchSemiTones = -3.5;
        provider.Rate = 0.95;

        Assert.Equal(1.35, provider.Tempo, 6);
        Assert.Equal(-3.5, provider.PitchSemiTones, 6);
        Assert.Equal(0.95, provider.Rate, 6);
    }

    [Fact]
    public void Read_ReturnsWholeFramesAndClearSupportsSeek()
    {
        var source = new TestSampleProvider(CreateSamples(32768), channels: 2);
        var provider = new SoundTouchSampleProvider(source);
        var output = new float[1024];

        int firstRead = provider.Read(output);
        Assert.True(firstRead > 0);
        Assert.Equal(0, firstRead % 2);

        source.Reset();
        provider.Clear();
        int readAfterSeek = provider.Read(output);

        Assert.True(readAfterSeek > 0);
        Assert.Equal(0, readAfterSeek % 2);
    }

    [Fact]
    public void Read_FadesOutTheEndOfTheSourceToSilence()
    {
        var samples = new float[44100 * 2 * 2];
        Array.Fill(samples, 1f);
        var provider = new SoundTouchSampleProvider(new TestSampleProvider(samples, channels: 2));
        var output = new List<float>();
        var chunk = new float[2880];

        int read;
        while ((read = provider.Read(chunk)) > 0)
            output.AddRange(chunk.AsSpan(0, read).ToArray());

        int lastFiveMilliseconds = 44100 * 5 / 1000 * 2;
        Assert.InRange(output[output.Count / 2], 0.95f, 1.05f);
        Assert.True(Math.Abs(output[^1]) < 1e-4f);
        for (int index = output.Count - lastFiveMilliseconds; index < output.Count; index++)
            Assert.True(Math.Abs(output[index]) < 0.3f, $"Sample {index} was not faded: {output[index]}");
    }

    [Fact]
    public void Constructor_RejectsNonFloatSource()
    {
        var source = new TestSampleProvider(CreateSamples(64), new WaveFormat(44100, 16, 2));

        Assert.Throws<ArgumentException>(() => new SoundTouchSampleProvider(source));
    }

    private static float[] CreateSamples(int count)
    {
        var samples = new float[count];
        for (int index = 0; index < samples.Length; index++)
            samples[index] = (float)Math.Sin(index * 0.03125);
        return samples;
    }

    private sealed class TestSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public TestSampleProvider(float[] samples, int channels)
            : this(samples, WaveFormat.CreateIeeeFloatWaveFormat(44100, channels))
        {
        }

        public TestSampleProvider(float[] samples, WaveFormat waveFormat)
        {
            _samples = samples;
            WaveFormat = waveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            int available = _samples.Length - _position;
            int count = Math.Min(buffer.Length, available);
            _samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public void Reset() => _position = 0;
    }
}
