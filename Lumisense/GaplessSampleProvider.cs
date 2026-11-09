using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Подставляет заранее подготовленный следующий источник без остановки выхода; с crossfadeFrames > 0 сводит оба по равной мощности.
// Switched и PreviousFinished приходят с audio thread; Disarm отдаёт токен, только если переключения ещё не было.
public sealed class GaplessSampleProvider : ISampleProvider
{
    private ISampleProvider _current;
    private readonly object _armLock = new();
    private ISampleProvider? _next;
    private object? _token;

    public GaplessSampleProvider(ISampleProvider first) => _current = first;

    public WaveFormat WaveFormat => _current.WaveFormat;

    public event Action<object>? Switched;

    // true — следующий источник принят и будет подставлен на конце текущего.
    public bool Arm(ISampleProvider next, object token)
    {
        var format = _current.WaveFormat;
        if (next.WaveFormat.SampleRate != format.SampleRate || next.WaveFormat.Channels != format.Channels
            || next.WaveFormat.Encoding != format.Encoding)
            return false;

        lock (_armLock)
        {
            if (_next is not null) return false;

            _next = next;
            _token = token;
            return true;
        }
    }

    public object? Disarm()
    {
        lock (_armLock)
        {
            object? token = _token;
            _next = null;
            _token = null;
            return token;
        }
    }

    public int Read(Span<float> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = _current.Read(buffer[total..]);
            if (read > 0)
            {
                total += read;
                continue;
            }

            ISampleProvider? next;
            object? token;
            lock (_armLock)
            {
                next = _next;
                token = _token;
                _next = null;
                _token = null;
            }

            if (next is null) break;

            _current = next;
            if (token is not null) Switched?.Invoke(token);
        }

        return total;
    }
}
