using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lumisense;

internal sealed record TrackExportOptions(
    double PlaybackSpeed,
    double PlaybackPitchSemitones,
    int BitRate = 192000);

internal sealed class TrackExportService
{
    private const int DefaultSampleRate = 44100;
    private const int MaxMp3Channels = 2;

    public Task ExportMp3Async(
        string sourcePath,
        string destinationPath,
        TrackExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() => ExportMp3Core(sourcePath, destinationPath, options, cancellationToken), cancellationToken);
    }

    private static void ExportMp3Core(
        string sourcePath,
        string destinationPath,
        TrackExportOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Destination path must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);

        string temporaryPath = destinationPath + ".partial";
        try
        {
            using var reader = new AudioFileReader(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();

            // Экспорт намеренно не подключает EqualizerSampleProvider: в MP3 должны попасть
            // только текущие скорость и тон, а EQ остаётся пользовательской настройкой live-playback.
            var tempo = new SoundTouchSampleProvider(reader)
            {
                Tempo = Math.Clamp(options.PlaybackSpeed, 0.5, 2.0),
                PitchSemiTones = Math.Clamp(options.PlaybackPitchSemitones, -12.0, 12.0)
            };
            IWaveProvider pcm = new SampleToWaveProvider16(tempo);
            IWaveProvider normalized = CreateMp3CompatibleProvider(pcm);
            using var normalizedDisposable = normalized as IDisposable;

            MediaFoundationEncoder.EncodeToMp3(normalized, temporaryPath, NormalizeBitRate(options.BitRate));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static IWaveProvider CreateMp3CompatibleProvider(IWaveProvider source)
    {
        int channels = source.WaveFormat.Channels;
        if (channels is < 1 or > MaxMp3Channels)
            throw new NotSupportedException("Экспорт MP3 поддерживает только mono и stereo.");

        bool formatIsMp3Compatible = source.WaveFormat.SampleRate is 32000 or 44100 or 48000;
        if (formatIsMp3Compatible)
            return new NonDisposingWaveProvider(source);

        var targetFormat = new WaveFormat(DefaultSampleRate, 16, channels);
        return new MediaFoundationResampler(source, targetFormat)
        {
            ResamplerQuality = 60
        };
    }

    private static int NormalizeBitRate(int bitRate) => bitRate switch
    {
        32000 or 40000 or 48000 or 56000 or 64000 or 80000 or 96000 or 112000 or
        128000 or 160000 or 192000 or 224000 or 256000 or 320000 => bitRate,
        _ => 192000
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось удалить временный файл экспорта '{path}': {ex.Message}");
        }
    }

    // MediaFoundationResampler.Dispose должен освобождать только свой wrapper; для совместимости
    // с разными версиями NAudio не передаём ему владение исходным provider после using reader.
    private sealed class NonDisposingWaveProvider(IWaveProvider source) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<byte> buffer) => source.Read(buffer);
    }
}
