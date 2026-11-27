using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Подставляет заранее подготовленный следующий источник без остановки выхода; с crossfadeFrames > 0 сводит оба по равной мощности.
// Switched и PreviousFinished приходят с audio thread; Disarm отдаёт токен, только если переключения ещё не было.
public sealed class GaplessSampleProvider : ISampleProvider
{
    private readonly object _armLock = new();
    private ISampleProvider _current;
    private Func<long>? _currentRemaining;

    private ISampleProvider? _next;
    private object? _token;
    private int _nextCrossfadeFrames;
    private Func<long>? _nextRemaining;

    private ISampleProvider? _previous;
    private Func<long>? _previousRemaining;
    private int _mixFrames;
    private float[] _scratch = Array.Empty<float>();

    // remainingFrames — сколько кадров осталось доиграть у текущего источника (нужно только для кроссфейда).
    public GaplessSampleProvider(ISampleProvider first, Func<long>? remainingFrames = null)
    {
        _current = first;
        _currentRemaining = remainingFrames;
    }

    public WaveFormat WaveFormat => _current.WaveFormat;

    public event Action<object>? Switched;
    public event Action? PreviousFinished;

    // true — следующий источник принят и будет подставлен.
    public bool Arm(ISampleProvider next, object token, int crossfadeFrames = 0, Func<long>? nextRemainingFrames = null)
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
            _nextCrossfadeFrames = Math.Max(0, crossfadeFrames);
            _nextRemaining = nextRemainingFrames;
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
            _nextRemaining = null;
            return token;
        }
    }

    public int Read(Span<float> buffer)
    {
        int channels = WaveFormat.Channels;
        int total = 0;
        while (total < buffer.Length)
        {
            if (_previous is not null)
            {
                int mixed = ReadMixing(buffer[total..], channels);
                total += mixed;
                if (mixed == 0 && _previous is null) continue;
                if (mixed == 0) break;
                continue;
            }

            long remaining = SafeRemaining(_currentRemaining);
            if (TryStartCrossfade(remaining)) continue;

            var span = buffer[total..];
            int crossfade = ArmedCrossfadeFrames();
            if (crossfade > 0 && remaining != long.MaxValue && remaining > crossfade)
            {
                // Не читаем дальше точки начала перекрытия, чтобы оно стартовало точно, а не на границе большого буфера.
                long frames = Math.Max(1, remaining - crossfade);
                int limit = (int)Math.Min(span.Length, frames * channels);
                span = span[..Math.Max(channels, limit)];
            }

            int read = _current.Read(span);
            if (read > 0)
            {
                total += read;
                continue;
            }

            if (!TrySwitchAtEnd()) break;
        }

        return total;
    }

    private int ArmedCrossfadeFrames()
    {
        lock (_armLock) return _next is null ? 0 : _nextCrossfadeFrames;
    }

    private static long SafeRemaining(Func<long>? remaining)
    {
        if (remaining is null) return long.MaxValue;

        try
        {
            return remaining();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return long.MaxValue;
        }
    }

    private bool TryTakeNext(out ISampleProvider next, out object? token, out int crossfade, out Func<long>? nextRemaining)
    {
        lock (_armLock)
        {
            next = _next!;
            token = _token;
            crossfade = _nextCrossfadeFrames;
            nextRemaining = _nextRemaining;
            if (_next is null) return false;

            _next = null;
            _token = null;
            _nextRemaining = null;
            return true;
        }
    }

    private bool TryStartCrossfade(long remaining)
    {
        int crossfade = ArmedCrossfadeFrames();
        if (crossfade <= 0 || _currentRemaining is null || remaining > crossfade) return false;
        if (!TryTakeNext(out var next, out var token, out int frames, out var nextRemaining)) return false;

        _previous = _current;
        _previousRemaining = _currentRemaining;
        _mixFrames = Math.Max(1, frames);
        _current = next;
        _currentRemaining = nextRemaining;
        if (token is not null) Switched?.Invoke(token);
        return true;
    }

    private bool TrySwitchAtEnd()
    {
        if (!TryTakeNext(out var next, out var token, out _, out var nextRemaining)) return false;

        _current = next;
        _currentRemaining = nextRemaining;
        if (token is not null) Switched?.Invoke(token);
        return true;
    }

    // Читает прежний источник в scratch, следующий — прямо в buffer и сводит их; прогресс считается по остатку прежнего источника.
    private int ReadMixing(Span<float> buffer, int channels)
    {
        if (_scratch.Length < buffer.Length) _scratch = new float[buffer.Length];

        long remainingAtStart = SafeRemaining(_previousRemaining);
        int previousRead = _previous!.Read(_scratch.AsSpan(0, buffer.Length));
        int nextRead = _current.Read(buffer);
        int count = Math.Max(previousRead, nextRead);

        for (int i = 0; i < count; i++)
        {
            double progress = remainingAtStart == long.MaxValue ? 1 : Math.Clamp(1.0 - (remainingAtStart - i / channels) / (double)_mixFrames, 0, 1);
            double angle = progress * Math.PI / 2;
            float outgoing = i < previousRead ? _scratch[i] * (float)Math.Cos(angle) : 0f;
            float incoming = i < nextRead ? buffer[i] * (float)Math.Sin(angle) : 0f;
            buffer[i] = outgoing + incoming;
        }

        if (previousRead < buffer.Length || remainingAtStart <= count / channels)
        {
            _previous = null;
            _previousRemaining = null;
            PreviousFinished?.Invoke();
        }

        return count;
    }
}
