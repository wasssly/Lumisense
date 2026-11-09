using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Громкость в конце цепочки: эквалайзер и баланс работают на полном сигнале, значение меняется плавно (~5 мс).
// Перемотка идёт на audio thread: старый звук затухает за 10 мс, новый нарастает за 8 мс.
public sealed class MasterVolumeSampleProvider : ISampleProvider
{
    private const double SmoothingMilliseconds = 5.0;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly double _coefficient;
    private float _target;
    private double _current;

    public MasterVolumeSampleProvider(ISampleProvider source, float initialGain)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);
        _coefficient = 1.0 - Math.Exp(-1.0 / (SmoothingMilliseconds / 1000.0 * source.WaveFormat.SampleRate));
        _target = Math.Max(initialGain, 0f);
        _current = _target;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Пишет UI-поток, читает audio thread.
    public float Gain
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, Math.Max(value, 0f));
    }

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (samplesRead == 0) return 0;

        float target = Volatile.Read(ref _target);
        if (_current == target && target == 1f) return samplesRead;

        double current = _current;
        for (int n = 0; n < samplesRead; n++)
        {
            if (n % _channels == 0 && current != target)
            {
                current += (target - current) * _coefficient;
                if (Math.Abs(target - current) < 1e-6) current = target;
            }

            buffer[n] = (float)(buffer[n] * current);
        }

        _current = current;
        return samplesRead;
    }
}
