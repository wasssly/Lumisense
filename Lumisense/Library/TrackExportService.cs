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
            int bitRate = NormalizeBitRate(options.BitRate);

            // LAME (см. LameMp3Encoder) не зависит от Media Foundation. Если недоступна,
            // TryEncode падает до чтения источника, поэтому откат ниже пересобирает цепочку с нуля.
            bool lameSucceeded;
            using (var pipeline = BuildExportPipeline(sourcePath, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lameSucceeded = LameMp3Encoder.TryEncode(pipeline.Normalized, temporaryPath, bitRate);
            }

            if (!lameSucceeded)
            {
                TryDelete(temporaryPath);
                using var pipeline = BuildExportPipeline(sourcePath, options);
                cancellationToken.ThrowIfCancellationRequested();
                MediaFoundationEncoder.EncodeToMp3(pipeline.Normalized, temporaryPath, bitRate);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch (Exception ex) when (IsMissingMp3EncoderError(ex))
        {
            TryDelete(temporaryPath);
            // Media Foundation не содержит MP3-кодировщика на этой системе; исходное сообщение
            // NAudio ничего не объясняет, поэтому даём понятный текст пользователю.
            throw new InvalidOperationException(
                "В Windows не установлен кодировщик MP3 (компонент Media Foundation). " +
                "На редакциях Windows N/KN это чинится установкой \"Media Feature Pack\" через " +
                "Параметры → Приложения → Дополнительные компоненты. Если такого пункта в списке нет — " +
                "скорее всего, у вас не N/KN редакция, и причина другая: возможно, Windows 11 LTSC " +
                "либо сторонняя программа для \"чистки\" системы удалила компоненты Windows Media Player.", ex);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    // Точный текст NAudio при отсутствующем в системе Media Foundation MP3-кодировщике.
    private static bool IsMissingMp3EncoderError(Exception ex) =>
        ex.Message.Contains("sink writer", StringComparison.OrdinalIgnoreCase);

    // Пересоздаётся с нуля на каждый вызов: SoundTouchSampleProvider держит внутреннее
    // буферное состояние, которое нельзя просто "перемотать назад".
    private static ExportPipeline BuildExportPipeline(string sourcePath, TrackExportOptions options)
    {
        var reader = new AudioFileReader(sourcePath);
        try
        {
            // Экспорт намеренно не подключает EqualizerSampleProvider: в MP3 должны попасть
            // только текущие скорость и тон, а EQ остаётся пользовательской настройкой live-playback.
            var tempo = new SoundTouchSampleProvider(reader)
            {
                Tempo = Math.Clamp(options.PlaybackSpeed, 0.5, 2.0),
                PitchSemiTones = Math.Clamp(options.PlaybackPitchSemitones, -12.0, 12.0)
            };
            IWaveProvider pcm = new SampleToWaveProvider16(tempo);
            IWaveProvider normalized = CreateMp3CompatibleProvider(pcm);

            IDisposable cleanup = normalized is IDisposable normalizedDisposable
                ? new CompositeDisposable(reader, normalizedDisposable)
                : reader;

            return new ExportPipeline(normalized, cleanup);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private readonly struct ExportPipeline(IWaveProvider normalized, IDisposable cleanup) : IDisposable
    {
        public IWaveProvider Normalized { get; } = normalized;
        public void Dispose() => cleanup.Dispose();
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items) item.Dispose();
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
