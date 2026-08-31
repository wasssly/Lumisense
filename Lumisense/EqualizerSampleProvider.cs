using System;
using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace Lumisense;

// N-полосный графический эквалайзер поверх ISampleProvider на BiQuadFilter.PeakingEQ.
// У каждой полосы отдельный фильтр на каждый канал — BiQuadFilter хранит состояние по
// предыдущим сэмплам, гонять оба канала через один и тот же фильтр нельзя, звук исказится.
public sealed class EqualizerSampleProvider : ISampleProvider
{
    public static readonly int[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public const double MinGainDb = -12.0;
    public const double MaxGainDb = 12.0;

    private const double Bandwidth = 0.9;
    private const double BypassTransitionMilliseconds = 8.0;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter[][] _filters;
    private readonly bool[] _bandSupportedBySource;
    private readonly double[] _gainsDb;
    private int _targetEnabled;
    private float _wetMix;

    // Переключение не обрывает sample stream: target меняется на UI-потоке, а audio thread
    // плавно доводит wet mix до нового значения за несколько миллисекунд.
    public bool Enabled
    {
        get => Volatile.Read(ref _targetEnabled) != 0;
        set => Interlocked.Exchange(ref _targetEnabled, value ? 1 : 0);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);

        _gainsDb = new double[BandFrequencies.Length];
        _filters = new BiQuadFilter[BandFrequencies.Length][];
        _bandSupportedBySource = new bool[BandFrequencies.Length];

        for (int band = 0; band < BandFrequencies.Length; band++)
        {
            _bandSupportedBySource[band] = BandFrequencies[band] < _source.WaveFormat.SampleRate / 2.0;
            if (!_bandSupportedBySource[band])
            {
                _filters[band] = Array.Empty<BiQuadFilter>();
                continue;
            }

            _filters[band] = new BiQuadFilter[_channels];
            for (int channel = 0; channel < _channels; channel++)
                _filters[band][channel] = MakeFilter(band, 0);
        }
    }

    private BiQuadFilter MakeFilter(int band, double gainDb) =>
        BiQuadFilter.PeakingEQ(_source.WaveFormat.SampleRate, BandFrequencies[band], (float)Bandwidth, (float)gainDb);

    public void SetBandGain(int band, double gainDb)
    {
        if (band < 0 || band >= BandFrequencies.Length) return;

        gainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        _gainsDb[band] = gainDb;
        if (!_bandSupportedBySource[band]) return;

        for (int channel = 0; channel < _channels; channel++)
            _filters[band][channel] = MakeFilter(band, gainDb);
    }

    public double GetBandGain(int band) => band >= 0 && band < _gainsDb.Length ? _gainsDb[band] : 0;

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (samplesRead == 0) return 0;

        float target = Volatile.Read(ref _targetEnabled) != 0 ? 1f : 0f;
        float mix = _wetMix;
        float step = 1f / Math.Max(1f,
            (float)(_source.WaveFormat.SampleRate * (BypassTransitionMilliseconds / 1000.0)));

        for (int n = 0; n < samplesRead; n++)
        {
            float dry = buffer[n];
            float wet = dry;
            int channel = n % _channels;

            for (int band = 0; band < _filters.Length; band++)
            {
                if (_bandSupportedBySource[band])
                    wet = _filters[band][channel].Transform(wet);
            }

            if (mix < target)
                mix = Math.Min(target, mix + step);
            else if (mix > target)
                mix = Math.Max(target, mix - step);

            buffer[n] = dry + (wet - dry) * mix;
        }

        _wetMix = mix;
        return samplesRead;
    }
}
