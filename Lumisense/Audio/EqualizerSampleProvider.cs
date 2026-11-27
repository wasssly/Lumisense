using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Усиления меняются плавно прямо в коэффициентах, поэтому ползунок не «моргает» и состояние фильтров не теряется.
// Авто-преамп убирает подъём АЧХ, чтобы не было клиппинга.
public sealed class EqualizerSampleProvider : ISampleProvider
{
    public static readonly int[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public const double MinGainDb = -12.0;
    public const double MaxGainDb = 12.0;

    private const double Bandwidth = 0.9;
    private const double BypassTransitionMilliseconds = 8.0;
    private const int BlockFrames = 32;
    private const double GainSmoothing = 0.12;
    private const double GainSnapDb = 0.004;
    private const int ResponseGridPoints = 128;

    private struct Biquad
    {
        public double B0, B1, B2, A1, A2;
    }

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly bool[] _bandSupportedBySource;
    private readonly double[] _targetGainsDb;
    private readonly double[] _currentGainsDb;
    private readonly Biquad[] _coefficients;
    private readonly double[,] _z1;
    private readonly double[,] _z2;
    private readonly int[] _active;
    private int _activeCount;
    private readonly object _gainLock = new();

    private int _targetEnabled;
    private float _wetMix;
    private int _autoHeadroom;
    private double _peakBoostDb;
    private double _currentPreamp = 1.0;

    // true, если фильтры не работали (эквалайзер был выключен): состояние осталось от старого звука и перед включением сбрасывается.
    private bool _filtersStale = true;

    // Переключение не обрывает sample stream: target меняется на UI-потоке, а audio thread
    // плавно доводит wet mix до нового значения за несколько миллисекунд.
    public bool Enabled
    {
        get => Volatile.Read(ref _targetEnabled) != 0;
        set => Interlocked.Exchange(ref _targetEnabled, value ? 1 : 0);
    }

    // Авто-преамп: общий звук ослабляется на величину максимального подъёма АЧХ (PeakBoostDb).
    public bool AutoHeadroom
    {
        get => Volatile.Read(ref _autoHeadroom) != 0;
        set => Interlocked.Exchange(ref _autoHeadroom, value ? 1 : 0);
    }

    // Максимальный положительный подъём суммарной АЧХ при заданных усилениях, дБ (0, если подъёма нет).
    public double PeakBoostDb
    {
        get { lock (_gainLock) return _peakBoostDb; }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);
        _sampleRate = source.WaveFormat.SampleRate;

        int bands = BandFrequencies.Length;
        _targetGainsDb = new double[bands];
        _currentGainsDb = new double[bands];
        _coefficients = new Biquad[bands];
        _z1 = new double[bands, _channels];
        _z2 = new double[bands, _channels];
        _active = new int[bands];
        _bandSupportedBySource = new bool[bands];

        for (int band = 0; band < bands; band++)
        {
            _bandSupportedBySource[band] = BandFrequencies[band] < _sampleRate / 2.0;
            _coefficients[band] = MakePeaking(_sampleRate, BandFrequencies[band], 0);
        }
    }

    public void SetBandGain(int band, double gainDb)
    {
        if (band < 0 || band >= BandFrequencies.Length) return;

        gainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        lock (_gainLock)
        {
            _targetGainsDb[band] = gainDb;
            _peakBoostDb = ComputePeakBoostDb();
        }
    }

    public double GetBandGain(int band)
    {
        if (band < 0 || band >= BandFrequencies.Length) return 0;
        lock (_gainLock) return _targetGainsDb[band];
    }

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (samplesRead == 0) return 0;

        float target = Volatile.Read(ref _targetEnabled) != 0 ? 1f : 0f;

        // Выключен и плавный переход закончен: звук идёт как есть, считать фильтры незачем.
        if (target == 0f && _wetMix <= 0f)
        {
            _filtersStale = true;
            return samplesRead;
        }

        // Первый буфер после выключенного эквалайзера: усиления берутся сразу, состояние фильтров сбрасывается,
        // а нарастание wet mix скрывает начало.
        if (_filtersStale)
        {
            SnapToTargets();
            _filtersStale = false;
        }

        float mix = _wetMix;
        float mixStep = 1f / Math.Max(1f, (float)(_sampleRate * (BypassTransitionMilliseconds / 1000.0)));
        int blockSamples = BlockFrames * _channels;

        for (int start = 0; start < samplesRead; start += blockSamples)
        {
            UpdateBlockCoefficients(out double preamp);
            int end = Math.Min(samplesRead, start + blockSamples);

            for (int n = start; n < end; n++)
            {
                double dry = buffer[n];
                double wet = dry;
                int channel = n % _channels;

                for (int i = 0; i < _activeCount; i++)
                {
                    int band = _active[i];
                    ref Biquad c = ref _coefficients[band];
                    double z1 = _z1[band, channel];
                    double y = c.B0 * wet + z1;
                    _z1[band, channel] = c.B1 * wet - c.A1 * y + _z2[band, channel];
                    _z2[band, channel] = c.B2 * wet - c.A2 * y;
                    wet = y;
                }

                wet *= preamp;

                if (mix < target)
                    mix = Math.Min(target, mix + mixStep);
                else if (mix > target)
                    mix = Math.Max(target, mix - mixStep);

                buffer[n] = (float)(dry + (wet - dry) * mix);
            }
        }

        _wetMix = mix;
        return samplesRead;
    }

    // Один раз на блок: плавно двигает усиления и преамп к целям и пересчитывает коэффициенты только у изменившихся полос.
    private void UpdateBlockCoefficients(out double preamp)
    {
        double[] targets;
        double peakBoost;
        lock (_gainLock)
        {
            targets = (double[])_targetGainsDb.Clone();
            peakBoost = _peakBoostDb;
        }

        int count = 0;
        for (int band = 0; band < targets.Length; band++)
        {
            if (!_bandSupportedBySource[band]) continue;

            double current = _currentGainsDb[band];
            double goal = targets[band];
            if (current != goal)
            {
                double next = current + (goal - current) * GainSmoothing;
                if (Math.Abs(goal - next) < GainSnapDb) next = goal;
                _currentGainsDb[band] = next;
                _coefficients[band] = MakePeaking(_sampleRate, BandFrequencies[band], next);
                current = next;
            }

            if (current == 0 && goal == 0)
            {
                // Полоса стала плоской: хвост состояния отбрасывается, дальше она не считается.
                for (int channel = 0; channel < _channels; channel++)
                {
                    _z1[band, channel] = 0;
                    _z2[band, channel] = 0;
                }
                continue;
            }

            _active[count++] = band;
        }

        _activeCount = count;

        double preampTarget = AutoHeadroom ? Math.Pow(10, -peakBoost / 20.0) : 1.0;
        if (_currentPreamp != preampTarget)
        {
            double next = _currentPreamp + (preampTarget - _currentPreamp) * GainSmoothing;
            _currentPreamp = Math.Abs(preampTarget - next) < 1e-5 ? preampTarget : next;
        }

        preamp = _currentPreamp;
    }

    private void SnapToTargets()
    {
        double[] targets;
        double peakBoost;
        lock (_gainLock)
        {
            targets = (double[])_targetGainsDb.Clone();
            peakBoost = _peakBoostDb;
        }

        for (int band = 0; band < targets.Length; band++)
        {
            _currentGainsDb[band] = targets[band];
            _coefficients[band] = MakePeaking(_sampleRate, BandFrequencies[band], targets[band]);
            for (int channel = 0; channel < _channels; channel++)
            {
                _z1[band, channel] = 0;
                _z2[band, channel] = 0;
            }
        }

        _currentPreamp = AutoHeadroom ? Math.Pow(10, -peakBoost / 20.0) : 1.0;
    }

    // Сумма АЧХ всех полос на логарифмической сетке; берётся максимум (не меньше 0 дБ).
    private double ComputePeakBoostDb()
    {
        double maxFrequency = Math.Min(20000.0, _sampleRate * 0.45);
        double minFrequency = 20.0;
        double peak = 0;

        for (int point = 0; point < ResponseGridPoints; point++)
        {
            double frequency = minFrequency * Math.Pow(maxFrequency / minFrequency, point / (double)(ResponseGridPoints - 1));
            double w = 2 * Math.PI * frequency / _sampleRate;
            double totalDb = 0;
            for (int band = 0; band < BandFrequencies.Length; band++)
            {
                if (!_bandSupportedBySource[band] || _targetGainsDb[band] == 0) continue;
                totalDb += MagnitudeDb(MakePeaking(_sampleRate, BandFrequencies[band], _targetGainsDb[band]), w);
            }

            if (totalDb > peak) peak = totalDb;
        }

        return peak;
    }

    private static double MagnitudeDb(Biquad c, double w)
    {
        double cos1 = Math.Cos(w), sin1 = Math.Sin(w);
        double cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);
        double numRe = c.B0 + c.B1 * cos1 + c.B2 * cos2;
        double numIm = -(c.B1 * sin1 + c.B2 * sin2);
        double denRe = 1 + c.A1 * cos1 + c.A2 * cos2;
        double denIm = -(c.A1 * sin1 + c.A2 * sin2);
        double ratio = (numRe * numRe + numIm * numIm) / Math.Max(denRe * denRe + denIm * denIm, 1e-30);
        return 10 * Math.Log10(Math.Max(ratio, 1e-30));
    }

    // RBJ peaking EQ; та же формула и Q, что были у NAudio BiQuadFilter.PeakingEQ.
    private static Biquad MakePeaking(int sampleRate, double frequency, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40.0);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * Bandwidth);

        double b0 = 1 + alpha * a;
        double b1 = -2 * cosW0;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        double a1 = -2 * cosW0;
        double a2 = 1 - alpha / a;

        return new Biquad { B0 = b0 / a0, B1 = b1 / a0, B2 = b2 / a0, A1 = a1 / a0, A2 = a2 / a0 };
    }
}
