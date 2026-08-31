using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace Lumisense;

internal sealed class PreparedTrack : IDisposable
{
    public required AudioFileReader AudioFile { get; init; }
    public required SoundTouchSampleProvider TempoProvider { get; init; }
    public required EqualizerSampleProvider Equalizer { get; init; }
    public required double ReplayGainFactor { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public BitmapImage? AlbumArt { get; init; }
    public byte[]? AlbumArtBytes { get; init; }
    public string? AlbumArtMimeType { get; init; }
    public TagLib.PictureType? AlbumArtPictureType { get; init; }

    public void Dispose()
    {
        try
        {
            AudioFile.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось освободить подготовленный AudioFileReader", ex);
        }
    }
}

internal sealed record TrackPreparationOptions(
    double VolumeSliderValue,
    bool UseLogarithmicVolume,
    bool ReplayGainEnabled,
    bool EqualizerEnabled,
    double[] EqualizerGains,
    double PlaybackSpeed,
    double PlaybackPitch,
    bool TraceEnabled);

internal sealed class TrackPreparationService
{
    private const double MinVolumeDb = -40.0;
    private const int ArtworkDisplayDecodePixelWidth = 512;

    public async Task<PreparedTrack> PrepareAsync(
        string filePath,
        TrackPreparationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);

        return await Task.Run(() => PrepareCore(filePath, options, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
    }

    private static PreparedTrack PrepareCore(
        string filePath,
        TrackPreparationOptions options,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long replayGainMilliseconds = 0;
        double replayGain = 1.0;

        string? title = null;
        string? artist = null;
        BitmapImage? albumArt = null;
        byte[]? albumArtBytes = null;
        string? albumArtMimeType = null;
        TagLib.PictureType? albumArtPictureType = null;
        long tagsMilliseconds = 0;
        long embeddedArtworkMilliseconds = 0;
        bool tagsMeasured = false;

        var tagsTimer = Stopwatch.StartNew();
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            if (options.ReplayGainEnabled)
            {
                var replayGainTimer = Stopwatch.StartNew();
                replayGain = ReplayGainReader.GetTrackGainLinear(tagFile.Tag);
                replayGainMilliseconds = replayGainTimer.ElapsedMilliseconds;
            }
            title = tagFile.Tag.Title;
            artist = !string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer)
                ? tagFile.Tag.FirstPerformer
                : tagFile.Tag.FirstAlbumArtist;
            tagsMilliseconds = tagsTimer.ElapsedMilliseconds;
            tagsMeasured = true;
            if (tagFile.Tag.Pictures.Length > 0)
            {
                var artworkTimer = Stopwatch.StartNew();
                var picture = tagFile.Tag.Pictures[0];
                albumArtBytes = picture.Data.Data;
                using var stream = new MemoryStream(albumArtBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = ArtworkDisplayDecodePixelWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                albumArt = bitmap;
                albumArtMimeType = string.IsNullOrWhiteSpace(picture.MimeType)
                    ? "image/jpeg"
                    : picture.MimeType;
                albumArtPictureType = picture.Type;
                embeddedArtworkMilliseconds = artworkTimer.ElapsedMilliseconds;
            }
        }
        catch (Exception ex)
        {
            if (!tagsMeasured)
                tagsMilliseconds = tagsTimer.ElapsedMilliseconds;
            Logger.Warn($"Не удалось прочитать metadata или embedded cover для файла {filePath}: {ex.Message}");
        }

        token.ThrowIfCancellationRequested();
        var audioFileReaderTimer = Stopwatch.StartNew();
        var reader = new AudioFileReader(filePath)
        {
            Volume = ComputeAudioFileVolume(options.VolumeSliderValue, options.UseLogarithmicVolume, replayGain)
        };
        long audioFileReaderMilliseconds = audioFileReaderTimer.ElapsedMilliseconds;
        try
        {
            var pipelineTimer = Stopwatch.StartNew();
            var tempoProvider = new SoundTouchSampleProvider(reader)
            {
                Tempo = Math.Clamp(options.PlaybackSpeed, 0.5, 2.0),
                PitchSemiTones = Math.Clamp(options.PlaybackPitch, -12.0, 12.0)
            };
            var equalizer = new EqualizerSampleProvider(tempoProvider)
            {
                Enabled = options.EqualizerEnabled
            };
            for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
                equalizer.SetBandGain(band, band < options.EqualizerGains.Length ? options.EqualizerGains[band] : 0);
            long pipelineMilliseconds = pipelineTimer.ElapsedMilliseconds;

            if (options.TraceEnabled)
            {
                Logger.Info(TrackPreparationTraceFormatter.Format(
                    replayGainMilliseconds,
                    tagsMilliseconds,
                    embeddedArtworkMilliseconds,
                    audioFileReaderMilliseconds,
                    pipelineMilliseconds));
            }

            return new PreparedTrack
            {
                AudioFile = reader,
                TempoProvider = tempoProvider,
                Equalizer = equalizer,
                ReplayGainFactor = replayGain,
                Title = title,
                Artist = artist,
                AlbumArt = albumArt,
                AlbumArtBytes = albumArtBytes,
                AlbumArtMimeType = albumArtMimeType,
                AlbumArtPictureType = albumArtPictureType
            };
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static float ComputeAudioFileVolume(
        double sliderValue,
        bool useLogarithmicVolume,
        double replayGainFactor)
    {
        sliderValue = Math.Clamp(sliderValue, 0.0, 1.0);
        float outputVolume;
        if (!useLogarithmicVolume)
        {
            outputVolume = (float)sliderValue;
        }
        else if (sliderValue <= 0.0)
        {
            outputVolume = 0f;
        }
        else
        {
            double db = MinVolumeDb * (1.0 - sliderValue);
            double raw = Math.Pow(10.0, db / 20.0);
            double floor = Math.Pow(10.0, MinVolumeDb / 20.0);
            outputVolume = (float)((raw - floor) / (1.0 - floor));
        }

        return (float)(outputVolume * replayGainFactor);
    }
}
