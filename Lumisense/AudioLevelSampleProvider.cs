using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Небольшой измеритель RMS-уровня в выходной цепочке. Он не меняет samples и не влияет на
// звучание: значение и спектр нужны только визуальной реакции Now Playing и острова на музыку.
public sealed class AudioLevelSampleProvider : ISampleProvider
{
    private const long SpectrumStaleMs = 150, SpectrumRefreshMs = 8;

    private readonly ISampleProvider _source;
    private readonly SpectrumAnalyzer _analyzer;
    private readonly object _spectrumGate = new();
    private readonly float[] _spectrum = new float[SpectrumAnalyzer.Bands];
    private double _normalizedLevel;
    private long _lastDataAt, _analyzedAt;
    private bool _enabled = true;

    public AudioLevelSampleProvider(ISampleProvider source)
    {
        _source = source;
        _analyzer = new SpectrumAnalyzer(source.WaveFormat.SampleRate);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Уровень и спектр считаются только пока их кто-то читает (Now Playing с реагирующим фоном, остров с эквалайзером или фоном);
    // иначе RMS и анализ спектра шли бы по каждому буферу зря. Включается из UI-потока, читается в аудиопотоке: Volatile, как и уровень.
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set => Volatile.Write(ref _enabled, value);
    }

    // Поток аудиодрайвера записывает уровень, UI-поток читает его. Volatile не допускает
    // устаревшего значения без блокировки/выделений памяти в горячем Read-пути.
    public double NormalizedLevel => Volatile.Read(ref _normalizedLevel);

    // Спектр из 40 полос для острова. false, если звука давно не было: полоски тогда опускаются.
    // Анализ делается не чаще раза в несколько миллисекунд, остальным вызовам достаётся готовый результат.
    internal bool ReadSpectrum(float[] bands)
    {
        if (!Enabled) return false;

        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastDataAt) > SpectrumStaleMs) return false;

        lock (_spectrumGate)
        {
            if (now - _analyzedAt >= SpectrumRefreshMs)
            {
                _analyzer.Analyze(_spectrum);
                _analyzedAt = now;
            }

            Array.Copy(_spectrum, bands, SpectrumAnalyzer.Bands);
        }

        return true;
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        if (read <= 0)
        {
            Volatile.Write(ref _normalizedLevel, 0d);
            return read;
        }

        if (!Volatile.Read(ref _enabled))
        {
            if (Volatile.Read(ref _normalizedLevel) != 0d)
                Volatile.Write(ref _normalizedLevel, 0d);
            return read;
        }

        _analyzer.Push(buffer[..read], WaveFormat.Channels);
        Volatile.Write(ref _lastDataAt, Environment.TickCount64);

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
