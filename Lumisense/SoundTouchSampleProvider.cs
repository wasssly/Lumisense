using System;
using NAudio.Wave;
using SoundTouch;

namespace Lumisense;

/// <summary>
/// Применяет изменения tempo, pitch и rate из SoundTouch к IEEE-float sample pipeline NAudio 3.
/// Хранит собственный FIFO SoundTouch и очищает его при перемотке источника, чтобы данные до seek
/// не попали в новый участок трека. Последние миллисекунды трека затухают до нуля, чтобы обрыв на
/// ненулевом sample не давал щелчка.
/// При нейтральных параметрах (темп, высота и скорость равны 1) звук идёт мимо SoundTouch: библиотека и на 1.0 прогоняет
/// фильтры и поиск наложения. Решение принимается в начале трека и после Clear (в этот момент у процессора нет накопленных
/// данных); если параметры изменили посреди трека, процессор запускается с плавным переходом от прямого звука.
/// </summary>
internal sealed class SoundTouchSampleProvider : ISampleProvider
{
    private const int TailFadeMilliseconds = 20;
    private const double NeutralTolerance = 1e-9;

    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _processor;
    private readonly float[] _inputBuffer = new float[4096];
    private readonly object _sync = new();
    private readonly int _standardTailFadeFrames;
    private bool _isFlushed;
    private int _tailFadeFrames = -1;

    // Режим без обработки: _queue держит опережение длиной с затухание (иначе к концу файла нечем затухать), как FIFO SoundTouch.
    private bool _passthrough;
    private bool _decisionPending = true;
    private float[] _queue = Array.Empty<float>();
    private int _queueCount;

    // Плавный переход от прямого звука к обработанному: сухой кусок из очереди смешивается с началом выхода процессора.
    private float[]? _crossfadeDry;
    private int _crossfadeFrames;
    private int _crossfadePosition;

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
            _queueCount = 0;
            _crossfadeDry = null;
            _crossfadePosition = 0;
            _passthrough = false;
            _decisionPending = true;
        }
    }

    private bool IsNeutral() =>
        Math.Abs(_processor.Tempo - 1.0) < NeutralTolerance
        && Math.Abs(_processor.Pitch - 1.0) < NeutralTolerance
        && Math.Abs(_processor.Rate - 1.0) < NeutralTolerance;

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
                if (_decisionPending)
                {
                    _decisionPending = false;
                    _passthrough = IsNeutral();
                }
                else if (_passthrough && !IsNeutral())
                {
                    EnterProcessing(channels);
                }

                return _passthrough
                    ? ReadPassthrough(buffer, channels, requestedFrames)
                    : ReadProcessed(buffer, channels, requestedFrames);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Ошибка обработки темпа или pitch через SoundTouch", ex);
            throw;
        }
    }

    // Звук без обработки с опережением на длину затухания: пока источник не закончился, последние кадры не отдаются.
    private int ReadPassthrough(Span<float> buffer, int channels, int requestedFrames)
    {
        int tailSamples = _standardTailFadeFrames * channels;
        int wanted = requestedFrames * channels;
        int needed = tailSamples + wanted + _inputBuffer.Length;
        if (_queue.Length < needed)
            Array.Resize(ref _queue, needed);

        while (!_isFlushed && _queueCount < wanted + tailSamples)
        {
            int samplesRead = _source.Read(_inputBuffer);
            if (samplesRead <= 0)
            {
                _isFlushed = true;
                break;
            }

            int completeSamples = samplesRead - samplesRead % channels;
            if (completeSamples > 0)
            {
                Array.Copy(_inputBuffer, 0, _queue, _queueCount, completeSamples);
                _queueCount += completeSamples;
            }
        }

        int deliver = _isFlushed
            ? Math.Min(wanted, _queueCount)
            : Math.Min(wanted, Math.Max(0, _queueCount - tailSamples));
        deliver -= deliver % channels;

        if (deliver > 0)
        {
            _queue.AsSpan(0, deliver).CopyTo(buffer);
            Array.Copy(_queue, deliver, _queue, 0, _queueCount - deliver);
            _queueCount -= deliver;
        }

        buffer.Slice(deliver).Clear();
        int framesRead = deliver / channels;
        if (_isFlushed)
            ApplyTailFade(buffer, channels, framesRead, _queueCount / channels);

        return deliver;
    }

    // Параметры стали не нейтральными посреди трека: процессор стартует с нуля, поэтому накопленное опережение и обработка
    // сводятся плавным переходом, а не обрывом.
    private void EnterProcessing(int channels)
    {
        _processor.Clear();

        if (!_isFlushed)
        {
            int fadeFrames = Math.Min(_queueCount / channels, _standardTailFadeFrames);
            if (fadeFrames > 0)
            {
                _crossfadeDry = _queue.AsSpan(0, fadeFrames * channels).ToArray();
                _crossfadeFrames = fadeFrames;
                _crossfadePosition = 0;
            }
        }

        if (_queueCount > 0)
        {
            _processor.PutSamples(_queue.AsSpan(0, _queueCount), _queueCount / channels);
            _queueCount = 0;
        }

        if (_isFlushed)
            _processor.Flush();

        _passthrough = false;
    }

    private void ApplyCrossfade(Span<float> buffer, int channels, int framesRead)
    {
        float[] dry = _crossfadeDry!;
        for (int frame = 0; frame < framesRead && _crossfadePosition < _crossfadeFrames; frame++, _crossfadePosition++)
        {
            float wet = (_crossfadePosition + 1) / (float)(_crossfadeFrames + 1);
            for (int channel = 0; channel < channels; channel++)
            {
                int index = frame * channels + channel;
                buffer[index] = dry[_crossfadePosition * channels + channel] * (1f - wet) + buffer[index] * wet;
            }
        }

        if (_crossfadePosition >= _crossfadeFrames)
            _crossfadeDry = null;
    }

    private int ReadProcessed(Span<float> buffer, int channels, int requestedFrames)
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
        if (_crossfadeDry is not null)
            ApplyCrossfade(buffer, channels, framesRead);

        if (_isFlushed)
            ApplyTailFade(buffer, channels, framesRead, _processor.AvailableSamples);

        return framesRead * channels;
    }
}
