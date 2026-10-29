using System;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioLevelSampleProviderTests
{
    private sealed class ConstantSource : ISampleProvider
    {
        private readonly float _value;
        private readonly int _limit;
        private int _served;

        public ConstantSource(float value, int limit = int.MaxValue)
        {
            _value = value;
            _limit = limit;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, _limit - _served);
            if (count <= 0) return 0;

            buffer[..count].Fill(_value);
            _served += count;
            return count;
        }
    }

    [Fact]
    public void Silence_KeepsLevelAtZero()
    {
        var provider = new AudioLevelSampleProvider(new ConstantSource(0f));
        var buffer = new float[512];

        for (int i = 0; i < 20; i++) provider.Read(buffer);

        Assert.Equal(0.0, provider.NormalizedLevel);
    }

    [Fact]
    public void LoudSignal_RaisesLevelButNeverAboveOne()
    {
        var provider = new AudioLevelSampleProvider(new ConstantSource(0.9f));
        var buffer = new float[512];

        provider.Read(buffer);
        double afterFirstRead = provider.NormalizedLevel;
        for (int i = 0; i < 200; i++) provider.Read(buffer);

        Assert.InRange(afterFirstRead, 0.01, 1.0);
        Assert.InRange(provider.NormalizedLevel, afterFirstRead, 1.0);
    }

    [Fact]
    public void EndOfSource_ResetsLevelToZero()
    {
        // Источник отдаёт ровно один буфер, затем 0: уровень должен сброситься, а не «залипнуть» на последнем значении.
        var provider = new AudioLevelSampleProvider(new ConstantSource(0.9f, limit: 512));
        var buffer = new float[512];

        provider.Read(buffer);
        Assert.True(provider.NormalizedLevel > 0);

        int read = provider.Read(buffer);

        Assert.Equal(0, read);
        Assert.Equal(0.0, provider.NormalizedLevel);
    }

    [Fact]
    public void DoesNotModifySamples()
    {
        var provider = new AudioLevelSampleProvider(new ConstantSource(0.25f));
        var buffer = new float[64];

        provider.Read(buffer);

        Assert.All(buffer, sample => Assert.Equal(0.25f, sample));
    }

    [Fact]
    public void Disabled_SkipsLevelCalculationButPassesSamplesThrough()
    {
        var provider = new AudioLevelSampleProvider(new ConstantSource(0.9f)) { Enabled = false };
        var buffer = new float[256];

        int read = provider.Read(buffer);

        Assert.Equal(buffer.Length, read);
        Assert.Equal(0.0, provider.NormalizedLevel);
        Assert.All(buffer, sample => Assert.Equal(0.9f, sample));
    }

    [Fact]
    public void Disabling_ResetsLevelAndReenablingResumesMeasuring()
    {
        var provider = new AudioLevelSampleProvider(new ConstantSource(0.9f));
        var buffer = new float[256];

        provider.Read(buffer);
        Assert.True(provider.NormalizedLevel > 0);

        provider.Enabled = false;
        provider.Read(buffer);
        Assert.Equal(0.0, provider.NormalizedLevel);

        provider.Enabled = true;
        provider.Read(buffer);
        Assert.True(provider.NormalizedLevel > 0);
    }

    [Fact]
    public void Disabled_ReportsNoSpectrum_AndEnabledReportsItForLiveAudio()
    {
        var provider = new AudioLevelSampleProvider(new ConstantSource(0.5f));
        var buffer = new float[2048];
        var bands = new float[SpectrumAnalyzer.Bands];

        provider.Read(buffer);
        Assert.True(provider.ReadSpectrum(bands));

        provider.Enabled = false;
        provider.Read(buffer);
        Assert.False(provider.ReadSpectrum(bands));
    }
}
