using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Прозрачный лимитер с упреждением 2 мс: ослабляет только пики выше −0,1 dBFS.
// Задержка не сдвигает трек: хвост отдаётся из линии задержки, длина звука не меняется.
public sealed class LimiterSampleProvider : ISampleProvider
{
    public const float Ceiling = 0.9886f;

    private const double LookaheadMilliseconds = 2.0;
    private const double ReleaseMilliseconds = 100.0;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _lookahead;
    private readonly float[] _delay;
    private int _delayPosition;
    private readonly float[] _windowValues;
    private readonly long[] _windowIndexes;
    private int _windowHead;
    private int _windowCount;
    private long _frameIndex;
    private double _gain = 1.0;
    private readonly double _attackStep;
    private readonly double _releaseStep;
    private int _enabled = 1;
    private bool _primed;
    private bool _needsFlush;
    private int _flushRemaining;

    public LimiterSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);
        int sampleRate = source.WaveFormat.SampleRate;
        _lookahead = Math.Max(1, (int)(sampleRate * LookaheadMilliseconds / 1000.0));
        _delay = new float[_lookahead * _channels];
        _windowValues = new float[_lookahead + 1];
        _windowIndexes = new long[_lookahead + 1];
        _attackStep = 1.0 / _lookahead;
        _releaseStep = 1.0 / Math.Max(1.0, sampleRate * ReleaseMilliseconds / 1000.0);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Interlocked.Exchange(ref _enabled, value ? 1 : 0);
    }

    public int Read(Span<float> buffer)
    {
        if (!_primed)
        {
            _primed = true;
            Prime();
        }

        int samplesRead = _source.Read(buffer);
        if (samplesRead == 0) return Flush(buffer);

        _needsFlush = true;
        _flushRemaining = _lookahead;
        Process(buffer, samplesRead / _channels, Enabled);
        return samplesRead;
    }

    // Заполняет линию задержки первыми кадрами источника; результат не выдаётся (там была бы тишина).
    private void Prime()
    {
        var temp = new float[_lookahead * _channels];
        int filled = 0;
        while (filled < temp.Length)
        {
            int read = _source.Read(temp.AsSpan(filled));
            if (read <= 0) break;
            filled += read;
        }

        if (filled == 0) return;

        Process(temp, filled / _channels, Enabled);
        _needsFlush = true;
        _flushRemaining = _lookahead;
    }

    // Конец источника: отдаёт кадры, оставшиеся в линии задержки, прогоняя их через ту же обработку с нулевым входом.
    private int Flush(Span<float> buffer)
    {
        if (!_needsFlush) return 0;

        int frames = Math.Min(buffer.Length / _channels, _flushRemaining);
        if (frames <= 0)
        {
            _needsFlush = false;
            return 0;
        }

        buffer[..(frames * _channels)].Clear();
        Process(buffer, frames, Enabled);
        _flushRemaining -= frames;
        if (_flushRemaining <= 0) _needsFlush = false;
        return frames * _channels;
    }

    private void Process(Span<float> buffer, int frames, bool enabled)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * _channels;

            float peak = 0f;
            for (int c = 0; c < _channels; c++)
                peak = Math.Max(peak, Math.Abs(buffer[offset + c]));

            float required = enabled && peak > Ceiling ? Ceiling / peak : 1f;

            // Скользящий минимум требуемого усиления по последним _lookahead + 1 кадрам.
            while (_windowCount > 0 && _windowIndexes[_windowHead] <= _frameIndex - _lookahead - 1)
            {
                _windowHead = (_windowHead + 1) % _windowValues.Length;
                _windowCount--;
            }

            while (_windowCount > 0 && _windowValues[(_windowHead + _windowCount - 1) % _windowValues.Length] >= required)
                _windowCount--;
            int tail = (_windowHead + _windowCount) % _windowValues.Length;
            _windowValues[tail] = required;
            _windowIndexes[tail] = _frameIndex;
            _windowCount++;

            double target = _windowValues[_windowHead];
            if (target < _gain)
                _gain = Math.Max(target, _gain - _attackStep);
            else if (target > _gain)
                _gain = Math.Min(target, _gain + _releaseStep);

            int delayOffset = _delayPosition * _channels;
            for (int c = 0; c < _channels; c++)
            {
                float delayed = _delay[delayOffset + c];
                _delay[delayOffset + c] = buffer[offset + c];
                float output = (float)(delayed * _gain);
                if (enabled)
                    output = Math.Clamp(output, -Ceiling, Ceiling);
                buffer[offset + c] = output;
            }

            _delayPosition = (_delayPosition + 1) % _lookahead;
            _frameIndex++;
        }
    }
}
