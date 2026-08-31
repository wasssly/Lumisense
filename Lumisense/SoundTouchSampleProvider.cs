using System;
using NAudio.Wave;
using SoundTouch;

namespace Lumisense;

/// <summary>
/// Применяет изменения tempo, pitch и rate из SoundTouch к IEEE-float sample pipeline NAudio 3.
/// Хранит собственный FIFO SoundTouch и очищает его при перемотке источника, чтобы данные до seek
/// не попали в новый участок трека.
/// </summary>
internal sealed class SoundTouchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _processor;
    private readonly float[] _inputBuffer = new float[4096];
    private readonly object _sync = new();
    private bool _isFlushed;

    public SoundTouchSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || source.WaveFormat.BitsPerSample != 32)
        {
            throw new ArgumentException(
                "SoundTouch requires a 32-bit IEEE-float sample source.",
                nameof(source));
        }

        _processor = new SoundTouchProcessor
        {
            SampleRate = source.WaveFormat.SampleRate,
            Channels = source.WaveFormat.Channels,
            Tempo = 1.0,
            Pitch = 1.0,
            Rate = 1.0
        };
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double Tempo
    {
        get { lock (_sync) return _processor.Tempo; }
        set { lock (_sync) _processor.Tempo = value; }
    }

    public double PitchSemiTones
    {
        get { lock (_sync) return _processor.PitchSemiTones; }
        set { lock (_sync) _processor.PitchSemiTones = value; }
    }

    public double Rate
    {
        get { lock (_sync) return _processor.Rate; }
        set { lock (_sync) _processor.Rate = value; }
    }

    /// <summary>
    /// Удаляет отложенные SoundTouch samples после seek либо при переиспользовании аудиографа.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _processor.Clear();
            _isFlushed = false;
        }
    }

    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        int channels = WaveFormat.Channels;
        int requestedFrames = buffer.Length / channels;
        if (requestedFrames == 0)
            return 0;

        try
        {
            lock (_sync)
            {
                while (_processor.AvailableSamples < requestedFrames)
                {
                    int samplesRead = _source.Read(_inputBuffer);
                    if (samplesRead <= 0)
                    {
                        if (!_isFlushed)
                        {
                            _isFlushed = true;
                            _processor.Flush();
                        }

                        break;
                    }

                    int completeSamples = samplesRead - samplesRead % channels;
                    if (completeSamples > 0)
                    {
                        _processor.PutSamples(_inputBuffer.AsSpan(0, completeSamples), completeSamples / channels);
                    }
                }

                buffer.Clear();
                int framesRead = _processor.ReceiveSamples(buffer, requestedFrames);
                return framesRead * channels;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Ошибка обработки темпа или pitch через SoundTouch", ex);
            throw;
        }
    }
}
