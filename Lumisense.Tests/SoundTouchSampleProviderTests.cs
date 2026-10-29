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
    public void NeutralSettings_PassSamplesThroughUnchangedBeforeTheTailFade()
    {
        float[] source = CreateStereoSine(44100);
        var provider = new SoundTouchSampleProvider(new TestSampleProvider(source, channels: 2));

        List<float> output = Drain(provider, chunkSize: 2880);

        int fadeSamples = 44100 * 20 / 1000 * 2;
        Assert.Equal(source.Length, output.Count);
        for (int index = 0; index < source.Length - fadeSamples; index++)
            Assert.Equal(source[index], output[index]);
    }

    [Fact]
    public void NeutralSettings_KeepEverySampleWithTinyAndOddChunks()
    {
        float[] source = CreateStereoSine(5000);
        var provider = new SoundTouchSampleProvider(new TestSampleProvider(source, channels: 2));

        Assert.Equal(source.Length, Drain(provider, chunkSize: 6).Count);
    }

    [Fact]
    public void TrackShorterThanTheFade_IsFadedWithoutLosingSamples()
    {
        var samples = new float[200];
        Array.Fill(samples, 1f);
        var provider = new SoundTouchSampleProvider(new TestSampleProvider(samples, channels: 2));

        List<float> output = Drain(provider, chunkSize: 64);

        Assert.Equal(200, output.Count);
        Assert.True(output[0] < 1f);
        Assert.True(Math.Abs(output[^1]) < 1e-6f);
    }

    [Fact]
    public void ClearAtNeutralSettings_RestoresExactPassthroughAfterProcessing()
    {
        float[] source = CreateStereoSine(20000);
        var reader = new TestSampleProvider(source, channels: 2);
        var provider = new SoundTouchSampleProvider(reader) { Tempo = 1.25 };
        provider.Read(new float[2880]);

        provider.Tempo = 1.0;
        reader.Reset();
        provider.Clear();
        var output = new float[2880];
        int read = provider.Read(output);

        Assert.Equal(2880, read);
        for (int index = 0; index < read; index++)
            Assert.Equal(source[index], output[index]);
    }

    [Fact]
    public void PitchReturnedToZeroSemitones_CountsAsNeutral()
    {
        float[] source = CreateStereoSine(8000);
        var provider = new SoundTouchSampleProvider(new TestSampleProvider(source, channels: 2));
        provider.PitchSemiTones = 3;
        provider.PitchSemiTones = 0;
        provider.Clear();

        var output = new float[1000];
        int read = provider.Read(output);

        Assert.Equal(1000, read);
        for (int index = 0; index < read; index++)
            Assert.Equal(source[index], output[index]);
    }

    [Fact]
    public void TempoChangedMidTrack_KeepsProducingFiniteAudio()
    {
        float[] source = CreateStereoSine(44100);
        var provider = new SoundTouchSampleProvider(new TestSampleProvider(source, channels: 2));
        var output = new List<float>();
        var chunk = new float[2880];

        for (int index = 0; ; index++)
        {
            if (index == 3) provider.Tempo = 1.25;
            int read = provider.Read(chunk);
            if (read <= 0) break;
            output.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        Assert.NotEmpty(output);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
        // Первые три буфера ушли без обработки и совпадают с источником.
        for (int index = 0; index < 2880 * 3; index++)
            Assert.Equal(source[index], output[index]);
    }

    [Fact]
    public void Constructor_RejectsNonFloatSource()
    {
        var source = new TestSampleProvider(CreateSamples(64), new WaveFormat(44100, 16, 2));

        Assert.Throws<ArgumentException>(() => new SoundTouchSampleProvider(source));
    }

    private static float[] CreateStereoSine(int frames)
    {
        var samples = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            float value = 0.5f * (float)Math.Sin(0.05 * frame);
            samples[frame * 2] = value;
            samples[frame * 2 + 1] = value + 0.001f;
        }

        return samples;
    }

    private static List<float> Drain(SoundTouchSampleProvider provider, int chunkSize)
    {
        var output = new List<float>();
        var chunk = new float[chunkSize];
        int read;
        while ((read = provider.Read(chunk)) > 0)
            output.AddRange(chunk.AsSpan(0, read).ToArray());
        return output;
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
