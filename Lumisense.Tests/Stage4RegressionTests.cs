using System.IO;
using Lumisense;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace Lumisense.Tests;

public sealed class Stage4RegressionTests
{
    [Fact]
    public void PlaybackSnapshot_ProgressRatio_IsClampedToValidRange()
    {
        Assert.Equal(0, new PlaybackSnapshot(null, "", "", false, -10, 100).ProgressRatio);
        Assert.Equal(0, new PlaybackSnapshot(null, "", "", false, 10, 0).ProgressRatio);
        Assert.Equal(1, new PlaybackSnapshot(null, "", "", false, 150, 100).ProgressRatio);
        Assert.Equal(0.25, new PlaybackSnapshot(null, "", "", false, 25, 100).ProgressRatio);
    }

    [Fact]
    public void PlaybackStateStore_IdenticalSnapshot_DoesNotRaiseDuplicateNotification()
    {
        var store = new PlaybackStateStore();
        int notifications = 0;
        var snapshot = new PlaybackSnapshot("track.wav", "Title", "Artist", true, 10, 100);
        store.Changed += _ => notifications++;

        store.Publish(snapshot);
        store.Publish(snapshot);

        Assert.Equal(1, notifications);
        Assert.Equal(snapshot, store.Current);
    }

    [Fact]
    public void AudioOutputSession_InitializeWithoutAttach_ReportsInvalidLifecycle()
    {
        using var session = new AudioOutputSession();
        var source = new SilenceProvider(new WaveFormat(44100, 1)).ToSampleProvider();

        Assert.Throws<InvalidOperationException>(() => session.Initialize(source));
    }

    [Fact]
    public async Task TrackPreparationService_ClampsSpeedPitchAndEqualizerGains()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"lumisense-stage4-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(filePath, new WaveFormat(44100, 16, 1)))
            {
                writer.Write(new byte[44100 * 2], 0, 44100 * 2);
            }

            var service = new TrackPreparationService();
            var options = new TrackPreparationOptions(
                VolumeSliderValue: 0.5,
                UseLogarithmicVolume: false,
                ReplayGainEnabled: false,
                EqualizerEnabled: true,
                EqualizerGains: new[] { 99.0, -99.0 },
                PlaybackSpeed: 9.0,
                PlaybackPitch: -99.0,
                TraceEnabled: false);

            using PreparedTrack prepared = await service.PrepareAsync(
                filePath,
                options,
                TestContext.Current.CancellationToken);

            Assert.InRange(prepared.TempoProvider.Tempo, 1.999999, 2.000001);
            Assert.InRange(prepared.TempoProvider.PitchSemiTones, -12.000001, -11.999999);
            Assert.Equal(EqualizerSampleProvider.MaxGainDb, prepared.Equalizer.GetBandGain(0));
            Assert.Equal(EqualizerSampleProvider.MinGainDb, prepared.Equalizer.GetBandGain(1));
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
