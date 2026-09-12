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

            // LAME (см. LameMp3Encoder) не зависит от того, установлен ли в Windows системный
            // MP3-кодировщик Media Foundation — предпочитаем её, если рядом с приложением есть
            // libmp3lame.dll. Если её нет (обычная ситуация, пока никто не положил файл рядом,
            // см. комментарий класса) — TryEncode вернёт false ещё до чтения каких-либо данных
            // из источника (первым падает сам lame_init через DllNotFoundException), поэтому
            // откат на Media Foundation ниже начинает читать источник заново, целиком, а не с
            // середины — BuildExportPipeline пересоздаёт всю цепочку (reader/tempo/resampler) с
            // нуля, а не пытается перемотать уже частично прочитанную.
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
            // NAudio.MediaFoundationEncoder делегирует кодирование в MP3 системному Media
            // Foundation Windows. Его MP3-кодировщик отсутствует по умолчанию на редакциях
            // N/KN (Европа/Корея) без отдельно поставленного Media Feature Pack — но этот
            // пункт в "Дополнительных компонентах" в принципе показывается только на N/KN,
            // на обычных редакциях (Home/Pro и т.п.) его там нет вообще, даже если сама
            // ошибка всё равно возникает (например, на Windows 11 LTSC или после ручной
            // чистки системы сторонними "деблоат"-утилитами). Оригинальное сообщение NAudio
            // ("Was not able to create a sink writer for this file extension") ничего из
            // этого не объясняет и выглядит как баг плеера, а не системы.
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

    // Общая цепочка source → tempo/pitch → 16-bit PCM → приведение к MP3-совместимому
    // формату — нужна дважды: для попытки через LAME и, если та не удалась, для отдельной
    // попытки через Media Foundation (см. ExportMp3Core). Пересоздаётся с нуля при каждом
    // вызове, а не переиспользуется между попытками — SoundTouchSampleProvider держит
    // внутреннее буферное состояние, которое нельзя просто "перемотать назад".
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
