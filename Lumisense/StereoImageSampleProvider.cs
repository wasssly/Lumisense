using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Ширина стереобазы (боковая часть L−R) и кроссфид для наушников; параметры меняются плавно, в нейтрали буфер не трогается.
public sealed class StereoImageSampleProvider : ISampleProvider
{
    private const double CrossfeedCutoffHz = 700;
    private const float MaxCrossfeedMix = 0.5f;
    private const float Smoothing = 0.002f;

    private readonly ISampleProvider _source;
    private readonly float _lowPassCoefficient;
    private int _widthPercent = 100;
    private int _crossfeedPercent;
    private float _width = 1f, _crossfeed;
    private float _leftLow, _rightLow;

    public StereoImageSampleProvider(ISampleProvider source)
    {
        _source = source;
        _lowPassCoefficient = (float)(1 - Math.Exp(-2 * Math.PI * CrossfeedCutoffHz / source.WaveFormat.SampleRate));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    // 0–200; 100 — без изменений.
    public int WidthPercent
    {
        get => Volatile.Read(ref _widthPercent);
        set => Volatile.Write(ref _widthPercent, Math.Clamp(value, 0, 200));
    }

    // 0–100; 0 — выключен.
    public int CrossfeedPercent
    {
        get => Volatile.Read(ref _crossfeedPercent);
        set => Volatile.Write(ref _crossfeedPercent, Math.Clamp(value, 0, 100));
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        if (read <= 0 || WaveFormat.Channels != 2) return read;

        float targetWidth = WidthPercent / 100f;
        float targetCrossfeed = CrossfeedPercent / 100f * MaxCrossfeedMix;
        bool settled = Math.Abs(_width - 1f) < 1e-4f && Math.Abs(_crossfeed) < 1e-4f && targetWidth == 1f && targetCrossfeed == 0f;
        if (settled)
        {
            _width = 1f;
            _crossfeed = 0f;
            return read;
        }

        int frames = read / 2;
        for (int frame = 0; frame < frames; frame++)
        {
            _width += (targetWidth - _width) * Smoothing;
            _crossfeed += (targetCrossfeed - _crossfeed) * Smoothing;

            float left = buffer[frame * 2];
            float right = buffer[frame * 2 + 1];

            if (_width != 1f)
            {
                float mid = (left + right) * 0.5f;
                float side = (left - right) * 0.5f * _width;
                left = mid + side;
                right = mid - side;
            }

            if (_crossfeed > 1e-5f)
            {
                _leftLow += (left - _leftLow) * _lowPassCoefficient;
                _rightLow += (right - _rightLow) * _lowPassCoefficient;
                float direct = 1f / (1f + _crossfeed);
                float newLeft = (left + _crossfeed * _rightLow) * direct;
                float newRight = (right + _crossfeed * _leftLow) * direct;
                left = newLeft;
                right = newRight;
            }
            else
            {
                _leftLow = left;
                _rightLow = right;
            }

            buffer[frame * 2] = left;
            buffer[frame * 2 + 1] = right;
        }

        return read;
    }
}
