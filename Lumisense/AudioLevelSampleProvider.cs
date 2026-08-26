using System;
using System.Threading;
using NAudio.Wave;

namespace AudioPlayer;

// Небольшой измеритель RMS-уровня в выходной цепочке. Он не меняет samples и не влияет на
// звучание: значение используется только для визуальной реакции Now Playing на музыку.
public sealed class AudioLevelSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private double _normalizedLevel;

    public AudioLevelSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Поток аудиодрайвера записывает уровень, UI-поток читает его. Volatile не допускает
    // устаревшего значения без блокировки/выделений памяти в горячем Read-пути.
    public double NormalizedLevel => Volatile.Read(ref _normalizedLevel);

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        if (read <= 0)
        {
            Volatile.Write(ref _normalizedLevel, 0d);
            return read;
        }

        double sumSquares = 0;
        for (int index = 0; index < read; index++)
        {
            double sample = buffer[index];
            sumSquares += sample * sample;
        }

        double rms = Math.Sqrt(sumSquares / read);
        // Обычный музыкальный RMS редко приближается к 1. Усиливаем и ограничиваем уровень,
        // чтобы визуальная реакция была заметна и на мастеринге без экстремальной громкости.
        double target = Math.Clamp(rms * 5.0, 0.0, 1.0);
        double previous = Volatile.Read(ref _normalizedLevel);
        double smoothing = target > previous ? 0.22 : 0.055;
        Volatile.Write(ref _normalizedLevel, previous + (target - previous) * smoothing);
        return read;
    }
}
