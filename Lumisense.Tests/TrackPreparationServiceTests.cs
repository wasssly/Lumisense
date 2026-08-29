using System.IO;
using AudioPlayer;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class TrackPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_CancellationRequestedBeforeStart_DoesNotOpenFile()
    {
        var service = new TrackPreparationService();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.Cancel();

        TrackPreparationOptions options = CreateOptions();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PrepareAsync("missing-file.wav", options, cts.Token));
    }

    [Fact]
    public async Task PrepareAsync_MissingFile_PropagatesReaderError()
    {
        var service = new TrackPreparationService();
        TrackPreparationOptions options = CreateOptions();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.PrepareAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"),
                options,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrepareAsync_ValidWave_CreatesConfiguredPipeline()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"lumisense-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(filePath, new WaveFormat(44100, 16, 1)))
            {
                writer.Write(new byte[44100 * 2], 0, 44100 * 2);
            }

            var service = new TrackPreparationService();
            TrackPreparationOptions options = CreateOptions(
                volumeSliderValue: 0.75,
                equalizerEnabled: true,
                playbackSpeed: 1.25,
                playbackPitch: 3.0);

            using PreparedTrack prepared = await service.PrepareAsync(
                filePath,
                options,
                TestContext.Current.CancellationToken);

            Assert.Equal(filePath, prepared.AudioFile.FileName);
            Assert.InRange(prepared.AudioFile.TotalTime.TotalSeconds, 0.99, 1.01);
            Assert.Equal(1.0, prepared.ReplayGainFactor);
            Assert.True(prepared.Equalizer.Enabled);
            Assert.Equal(1.25, prepared.TempoProvider.Tempo);
            Assert.InRange(prepared.TempoProvider.PitchSemiTones, 2.999999, 3.000001);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task PreparedTrack_Dispose_CanBeCalledRepeatedly()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"lumisense-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(filePath, new WaveFormat(44100, 16, 1)))
            {
                writer.Write(new byte[44100 * 2], 0, 44100 * 2);
            }

            var service = new TrackPreparationService();
            PreparedTrack prepared = await service.PrepareAsync(
                filePath,
                CreateOptions(),
                TestContext.Current.CancellationToken);

            prepared.Dispose();
            prepared.Dispose();
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static TrackPreparationOptions CreateOptions(
        double volumeSliderValue = 0.5,
        bool equalizerEnabled = false,
        double playbackSpeed = 1.0,
        double playbackPitch = 0.0) =>
        new(
            volumeSliderValue,
            UseLogarithmicVolume: true,
            ReplayGainEnabled: false,
            EqualizerEnabled: equalizerEnabled,
            EqualizerGains: new double[EqualizerSampleProvider.BandFrequencies.Length],
            PlaybackSpeed: playbackSpeed,
            PlaybackPitch: playbackPitch,
            TraceEnabled: false);
}
