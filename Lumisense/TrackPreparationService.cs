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
    public int BitrateKbps { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public BitmapImage? AlbumArt { get; init; }
    public byte[]? AlbumArtBytes { get; init; }
    public string? AlbumArtMimeType { get; init; }
    public AlbumArtPictureKind? AlbumArtPictureType { get; init; }

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
    bool TraceEnabled,
    bool ReplayGainPreferAlbum = false,
    double ReplayGainPreampDb = 0);

internal sealed class TrackPreparationService
{
    private const double MinVolumeDb = -40.0; // тише практически не слышно — дальше просто тишина
    private const double LinearVolumeExponent = 2.0;
    internal const int ArtworkDisplayDecodePixelWidth = 512;

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
        string? album = null;
        BitmapImage? albumArt = null;
        byte[]? albumArtBytes = null;
        string? albumArtMimeType = null;
        AlbumArtPictureKind? albumArtPictureType = null;
        int bitrateKbps = 0;
        long tagsMilliseconds = 0;
        long embeddedArtworkMilliseconds = 0;
        bool tagsMeasured = false;

        var tagsTimer = Stopwatch.StartNew();
        try
        {
            var tags = TrackTagCache.GetOrLoad(filePath);
            if (options.ReplayGainEnabled)
            {
                var replayGainTimer = Stopwatch.StartNew();
                replayGain = ReplayGainReader.GetGainLinear(tags.Track, options.ReplayGainPreferAlbum, options.ReplayGainPreampDb);
                replayGainMilliseconds = replayGainTimer.ElapsedMilliseconds;
            }
            title = tags.Title;
            album = tags.Album;
            artist = tags.Artist;
            bitrateKbps = tags.BitrateKbps;
            albumArt = tags.AlbumArt;
            albumArtBytes = tags.AlbumArtBytes;
            albumArtMimeType = tags.AlbumArtMimeType;
            albumArtPictureType = tags.AlbumArtPictureType;
            tagsMilliseconds = tags.TagsMilliseconds;
            embeddedArtworkMilliseconds = tags.ArtworkMilliseconds;
            tagsMeasured = true;
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
            // Здесь только ReplayGain (до эквалайзера); громкость ползунка применяется в конце цепочки (MasterVolumeSampleProvider).
            Volume = (float)replayGain
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
                BitrateKbps = bitrateKbps,
                Title = title,
                Artist = artist,
                Album = album,
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

    // Положение ползунка (0..1) → множитель амплитуды (с учётом ReplayGain). Обычно мягкая audio-taper кривая, в
    // логарифмическом режиме — через децибелы [MinDb, 0] и 10^(dB/20), чтобы ход воспринимался равномерно.
    internal static float ComputeAudioFileVolume(
        double sliderValue,
        bool useLogarithmicVolume,
        double replayGainFactor)
    {
        sliderValue = Math.Clamp(sliderValue, 0.0, 1.0);
        float outputVolume;
        if (!useLogarithmicVolume)
        {
            // Небольшой audio taper делает верхний диапазон различимее на слух:
            // 70%, 80% и 100% больше не воспринимаются почти одинаково.
            outputVolume = (float)Math.Pow(sliderValue, LinearVolumeExponent);
        }
        else if (sliderValue <= 0.0)
        {
            outputVolume = 0f;
        }
        else
        {
            double db = MinVolumeDb * (1.0 - sliderValue);
            double raw = Math.Pow(10.0, db / 20.0);
            // Без перенормировки последний отрезок хода давал резкий скачок к тишине вместо плавного затухания.
            double floor = Math.Pow(10.0, MinVolumeDb / 20.0);
            outputVolume = (float)((raw - floor) / (1.0 - floor));
        }

        return (float)(outputVolume * replayGainFactor);
    }
}
