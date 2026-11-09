using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Громкость в конце цепочки: эквалайзер и баланс работают на полном сигнале, значение меняется плавно (~5 мс).
// Перемотка идёт на audio thread: старый звук затухает за 10 мс, новый нарастает за 8 мс.
public sealed class MasterVolumeSampleProvider : ISampleProvider
{
    private const double SmoothingMilliseconds = 5.0;
    private const double SeekFadeOutMilliseconds = 10.0;
    private const double SeekFadeInMilliseconds = 8.0;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly double _coefficient;
    private float _target;
    private double _current;
    private Action? _pendingSeek;
    private int _fadeInFramesLeft;
    private int _fadeInFramesTotal;

    public MasterVolumeSampleProvider(ISampleProvider source, float initialGain)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);
        _sampleRate = source.WaveFormat.SampleRate;
        _coefficient = 1.0 - Math.Exp(-1.0 / (SmoothingMilliseconds / 1000.0 * _sampleRate));
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

    // Перемотка выполнится на audio thread в ближайшем Read; новый запрос заменяет ещё не выполненный.
    public void RequestSeek(Action seek) => Interlocked.Exchange(ref _pendingSeek, seek);

    // Забирает невыполненную перемотку (например, если вывод остановлен и Read не вызывается); null, если уже выполнена.
    public Action? TakePendingSeek() => Interlocked.Exchange(ref _pendingSeek, null);

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (samplesRead == 0) return 0;

        float target = Volatile.Read(ref _target);
        Action? seek = Volatile.Read(ref _pendingSeek) is null ? null : Interlocked.Exchange(ref _pendingSeek, null);
        if (seek is null && _fadeInFramesLeft == 0 && _current == target && target == 1f) return samplesRead;

        int frames = samplesRead / _channels;
        int fadeOutFrames = seek is null ? 0 : Math.Min(frames, Math.Max(1, (int)(_sampleRate * SeekFadeOutMilliseconds / 1000.0)));
        int fadeOutStart = frames - fadeOutFrames;

        double current = _current;
        for (int n = 0; n < samplesRead; n++)
        {
            int frame = n / _channels;
            if (n % _channels == 0 && current != target)
            {
                current += (target - current) * _coefficient;
                if (Math.Abs(target - current) < 1e-6) current = target;
            }

            double factor = current;
            if (_fadeInFramesLeft > 0)
            {
                factor *= 1.0 - (double)_fadeInFramesLeft / _fadeInFramesTotal;
                if (n % _channels == _channels - 1) _fadeInFramesLeft--;
            }

            if (frame >= fadeOutStart && fadeOutFrames > 0)
                factor *= 1.0 - (double)(frame - fadeOutStart + 1) / fadeOutFrames;

            buffer[n] = (float)(buffer[n] * factor);
        }

        _current = current;

        if (seek is not null)
        {
            try
            {
                seek();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Перемотка на audio thread не удалась: {ex.Message}");
            }

            _fadeInFramesTotal = Math.Max(1, (int)(_sampleRate * SeekFadeInMilliseconds / 1000.0));
            _fadeInFramesLeft = _fadeInFramesTotal;
        }

        return samplesRead;
    }
}
