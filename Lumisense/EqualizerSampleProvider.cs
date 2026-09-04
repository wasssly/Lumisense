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
    private const double FilterTransitionMilliseconds = 8.0;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter[][] _filters;
    private readonly bool[] _bandSupportedBySource;
    private readonly double[] _gainsDb;
    private readonly object _filterUpdateLock = new();
    private double[]? _pendingGainsDb;
    private int _pendingFilterRebuild;
    private float _filterMix = 1f;
    private float _filterMixTarget = 1f;
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
        lock (_filterUpdateLock)
        {
            _gainsDb[band] = gainDb;
            _pendingGainsDb ??= (double[])_gainsDb.Clone();
            _pendingGainsDb[band] = gainDb;
            Volatile.Write(ref _pendingFilterRebuild, 1);
            Volatile.Write(ref _filterMixTarget, 0f);
        }
    }

    public double GetBandGain(int band) => band >= 0 && band < _gainsDb.Length ? _gainsDb[band] : 0;

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (samplesRead == 0) return 0;

        float target = Volatile.Read(ref _targetEnabled) != 0 ? 1f : 0f;
        float mix = _wetMix;
        float bypassStep = 1f / Math.Max(1f,
            (float)(_source.WaveFormat.SampleRate * (BypassTransitionMilliseconds / 1000.0)));
        float filterMix = _filterMix;
        float filterMixTarget = Volatile.Read(ref _filterMixTarget);
        float filterStep = 1f / Math.Max(1f,
            (float)(_source.WaveFormat.SampleRate * (FilterTransitionMilliseconds / 1000.0)));

        for (int n = 0; n < samplesRead; n++)
        {
            filterMixTarget = Volatile.Read(ref _filterMixTarget);
            if (filterMix < filterMixTarget)
                filterMix = Math.Min(filterMixTarget, filterMix + filterStep);
            else if (filterMix > filterMixTarget)
                filterMix = Math.Max(filterMixTarget, filterMix - filterStep);

            if (filterMix <= 0f && Volatile.Read(ref _pendingFilterRebuild) != 0)
                ApplyPendingFilters();

            float dry = buffer[n];
            float wet = dry;
            int channel = n % _channels;

            for (int band = 0; band < _filters.Length; band++)
            {
                if (_bandSupportedBySource[band])
                    wet = _filters[band][channel].Transform(wet);
            }

            float filtered = dry + (wet - dry) * filterMix;

            if (mix < target)
                mix = Math.Min(target, mix + bypassStep);
            else if (mix > target)
                mix = Math.Max(target, mix - bypassStep);

            buffer[n] = dry + (filtered - dry) * mix;
        }

        _filterMix = filterMix;
        _wetMix = mix;
        return samplesRead;
    }

    private void ApplyPendingFilters()
    {
        lock (_filterUpdateLock)
        {
            if (_pendingFilterRebuild == 0 || _pendingGainsDb is null)
                return;

            for (int band = 0; band < _filters.Length; band++)
            {
                if (!_bandSupportedBySource[band]) continue;
                double gainDb = _pendingGainsDb[band];
                for (int channel = 0; channel < _channels; channel++)
                    _filters[band][channel] = MakeFilter(band, gainDb);
            }

            _pendingFilterRebuild = 0;
            Volatile.Write(ref _filterMixTarget, 1f);
        }
    }
}
