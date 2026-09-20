using System;
using NAudio.Wave;
using SoundTouch;

namespace Lumisense;

/// <summary>
/// Применяет изменения tempo, pitch и rate из SoundTouch к IEEE-float sample pipeline NAudio 3.
/// Хранит собственный FIFO SoundTouch и очищает его при перемотке источника, чтобы данные до seek
/// не попали в новый участок трека. Последние миллисекунды трека затухают до нуля, чтобы обрыв на
/// ненулевом sample не давал щелчка.
/// </summary>
internal sealed class SoundTouchSampleProvider : ISampleProvider
{
    private const int TailFadeMilliseconds = 20;

    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _processor;
    private readonly float[] _inputBuffer = new float[4096];
    private readonly object _sync = new();
    private readonly int _standardTailFadeFrames;
    private bool _isFlushed;
    private int _tailFadeFrames = -1;

    public SoundTouchSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || source.WaveFormat.BitsPerSample != 32)
        {
            throw new ArgumentException(
                "SoundTouch requires a 32-bit IEEE-float sample source.",
                nameof(source));
        }

        _standardTailFadeFrames = source.WaveFormat.SampleRate * TailFadeMilliseconds / 1000;
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
            _tailFadeFrames = -1;
        }
    }

    // После flush в FIFO лежит весь оставшийся хвост трека, поэтому его длина известна точно.
    // Затухание по нему убирает щелчок от обрыва на ненулевом sample.
    private void ApplyTailFade(Span<float> buffer, int channels, int framesRead, int framesRemaining)
    {
        if (framesRead <= 0)
            return;

        // Трек короче затухания: часть уже отдана, поэтому затухаем на всём остатке без скачка.
        if (_tailFadeFrames < 0)
            _tailFadeFrames = Math.Max(1, Math.Min(_standardTailFadeFrames, framesRead + framesRemaining));

        int firstFadedFrame = Math.Max(0, framesRemaining + framesRead - _tailFadeFrames);
        for (int frame = firstFadedFrame; frame < framesRead; frame++)
        {
            float gain = (framesRemaining + framesRead - 1 - frame) / (float)_tailFadeFrames;
            int start = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                buffer[start + channel] *= gain;
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
                // До конца файла держим в FIFO запас в длину затухания: иначе к моменту, когда
                // конец обнаружен, последние кадры уже отданы и затухать будет нечему.
                while (_processor.AvailableSamples < requestedFrames + (_isFlushed ? 0 : _standardTailFadeFrames))
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
                if (_isFlushed)
                    ApplyTailFade(buffer, channels, framesRead, _processor.AvailableSamples);

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
