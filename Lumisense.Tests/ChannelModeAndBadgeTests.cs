using System;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class ChannelModeSampleProviderTests
{
    [Fact]
    public void Read_KeepsStereoUntouchedWhenMonoIsOff()
    {
        var provider = new ChannelModeSampleProvider(CreateSource(1f, 0f, 0.5f, -0.5f));
        var buffer = new float[4];

        int read = provider.Read(buffer);

        Assert.Equal(4, read);
        Assert.Equal(new[] { 1f, 0f, 0.5f, -0.5f }, buffer);
    }

    [Fact]
    public void Read_MixesEveryFrameIntoBothChannelsWhenMonoIsOn()
    {
        var provider = new ChannelModeSampleProvider(CreateSource(1f, 0f, 0.5f, -0.5f)) { MonoEnabled = true };
        var buffer = new float[4];

        provider.Read(buffer);

        Assert.Equal(new[] { 0.5f, 0.5f, 0f, 0f }, buffer);
    }

    [Fact]
    public void Read_LeavesAMonoSourceAlone()
    {
        var source = new TestSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 1), new[] { 0.25f, -0.75f });
        var provider = new ChannelModeSampleProvider(source) { MonoEnabled = true };
        var buffer = new float[2];

        provider.Read(buffer);

        Assert.Equal(new[] { 0.25f, -0.75f }, buffer);
    }

    private static TestSampleProvider CreateSource(params float[] samples) =>
        new(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), samples);

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

public sealed class TrackBadgeFormatterTests
{
    [Theory]
    [InlineData(@"C:\Music\song.flac", "FLAC")]
    [InlineData(@"C:\Music\song.Mp3", "MP3")]
    [InlineData(@"C:\Music\song", null)]
    [InlineData(null, null)]
    public void Extension_ReturnsUpperCaseFormatOrNull(string? path, string? expected) =>
        Assert.Equal(expected, TrackBadgeFormatter.Extension(path));

    [Theory]
    [InlineData(320, 44100, "320 kbps · 44.1 kHz")]
    [InlineData(0, 48000, "48 kHz")]
    [InlineData(256, 0, "256 kbps")]
    [InlineData(0, 0, null)]
    public void Quality_JoinsKnownPartsOnly(int bitrate, int sampleRate, string? expected) =>
        Assert.Equal(expected, TrackBadgeFormatter.Quality(bitrate, sampleRate));

    [Theory]
    [InlineData(320, "320 kbps")]
    [InlineData(0, null)]
    public void Bitrate_ReturnsKbpsOrNull(int bitrate, string? expected) =>
        Assert.Equal(expected, TrackBadgeFormatter.Bitrate(bitrate));

    [Theory]
    [InlineData(44100, "44.1 kHz")]
    [InlineData(96000, "96 kHz")]
    [InlineData(0, null)]
    public void SampleRate_ReturnsKhzOrNull(int sampleRate, string? expected) =>
        Assert.Equal(expected, TrackBadgeFormatter.SampleRate(sampleRate));

    [Fact]
    public void Join_SkipsMissingPartsAndReturnsNullWhenNothingRemains()
    {
        Assert.Equal("Playing · FLAC · 8.4 MB", TrackBadgeFormatter.Join("Playing", "FLAC", null, "", "8.4 MB"));
        Assert.Null(TrackBadgeFormatter.Join(null, ""));
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(512L * 1024, "512 KB")]
    [InlineData(8L * 1024 * 1024 + 400L * 1024, "8.4 MB")]
    public void FileSize_UsesKilobytesBelowOneMegabyte(long bytes, string? expected) =>
        Assert.Equal(expected, TrackBadgeFormatter.FileSize(bytes));
}
