using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// TPDF-дизеринг последним звеном цепочки: в монопольном режиме float округляется до 16/24 бит, и без шума
// квантования остаётся гармонической «зернистостью» на тихих местах. Нуль в TargetBits — без обработки.
public sealed class DitherSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private int _targetBits;
    private uint _state = 0x9E3779B9u;

    public DitherSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Разрядность устройства: дизерим только 16 и 24 бита, остальное (float, 32) оставляем как есть.
    public int TargetBits
    {
        get => Volatile.Read(ref _targetBits);
        set => Volatile.Write(ref _targetBits, value is 16 or 24 ? value : 0);
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        int bits = TargetBits;
        if (read <= 0 || bits == 0) return read;

        Span<float> samples = buffer[..read];
        if (IsSilent(samples)) return read;

        // Один шаг младшего разряда: амплитуда треугольного шума — ±1 LSB.
        float lsb = 1f / (1 << (bits - 1));
        for (int i = 0; i < samples.Length; i++)
        {
            float noise = (NextUniform() - NextUniform()) * lsb;
            samples[i] = Math.Clamp(samples[i] + noise, -1f, 1f);
        }

        return read;
    }

    // Цифровая тишина (пауза, начало fade-in) остаётся тишиной, а не шипением на −96 dBFS.
    private static bool IsSilent(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
            if (sample != 0f) return false;
        return true;
    }

    // xorshift32: равномерное число в [0, 1).
    private float NextUniform()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return (x >> 8) * (1f / (1 << 24));
    }
}
