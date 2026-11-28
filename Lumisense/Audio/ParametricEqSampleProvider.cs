using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Каскад биквадов по профилю AutoEq. Профиль меняется на UI-потоке, поток звука подхватывает его на границе буфера
// и плавно возвращает wet mix, чтобы замена не щёлкала.
public sealed class ParametricEqSampleProvider : ISampleProvider
{
    private const double TransitionMilliseconds = 8.0;

    private struct Biquad
    {
        public double B0, B1, B2, A1, A2;
    }

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private ParametricEqProfile? _requested;
    private ParametricEqProfile? _applied;
    private Biquad[] _filters = Array.Empty<Biquad>();
    private double[] _z1 = Array.Empty<double>();
    private double[] _z2 = Array.Empty<double>();
    private double _preamp = 1.0;
    private int _enabled;
    private float _wetMix;

    internal ParametricEqProfile? Profile
    {
        get => Volatile.Read(ref _requested);
        set => Volatile.Write(ref _requested, value);
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Interlocked.Exchange(ref _enabled, value ? 1 : 0);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public ParametricEqSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);
        _sampleRate = source.WaveFormat.SampleRate;
    }

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (samplesRead == 0) return 0;

        ParametricEqProfile? requested = Volatile.Read(ref _requested);
        float target = Volatile.Read(ref _enabled) != 0 && requested is not null ? 1f : 0f;
        if (target == 0f && _wetMix <= 0f) return samplesRead;

        if (!ReferenceEquals(requested, _applied))
        {
            Rebuild(requested);
            _wetMix = 0f;
        }

        if (_filters.Length == 0 && target == 0f) return samplesRead;

        float mix = _wetMix;
        float step = 1f / Math.Max(1f, (float)(_sampleRate * (TransitionMilliseconds / 1000.0)));
        int count = _filters.Length;

        for (int n = 0; n < samplesRead; n++)
        {
            double dry = buffer[n];
            double wet = dry;
            int channel = n % _channels;

            for (int i = 0; i < count; i++)
            {
                ref Biquad c = ref _filters[i];
                int state = i * _channels + channel;
                double y = c.B0 * wet + _z1[state];
                _z1[state] = c.B1 * wet - c.A1 * y + _z2[state];
                _z2[state] = c.B2 * wet - c.A2 * y;
                wet = y;
            }

            wet *= _preamp;

            if (mix < target) mix = Math.Min(target, mix + step);
            else if (mix > target) mix = Math.Max(target, mix - step);

            buffer[n] = (float)(dry + (wet - dry) * mix);
        }

        _wetMix = mix;
        return samplesRead;
    }

    private void Rebuild(ParametricEqProfile? profile)
    {
        var list = new System.Collections.Generic.List<Biquad>();
        if (profile is not null)
        {
            foreach (ParametricFilter filter in profile.Filters)
            {
                // Выше частоты Найквиста фильтр не имеет смысла: частота дискретизации источника может быть низкой.
                if (filter.FrequencyHz >= _sampleRate * 0.49) continue;
                list.Add(Design(filter, _sampleRate));
            }
        }

        _filters = list.ToArray();
        _z1 = new double[_filters.Length * _channels];
        _z2 = new double[_filters.Length * _channels];
        _preamp = profile is null ? 1.0 : Math.Pow(10, profile.PreampDb / 20.0);
        _applied = profile;
    }

    // RBJ Audio EQ Cookbook: peaking, low shelf и high shelf с заданной добротностью.
    internal static (double B0, double B1, double B2, double A1, double A2) Coefficients(ParametricFilter filter, int sampleRate)
    {
        Biquad c = Design(filter, sampleRate);
        return (c.B0, c.B1, c.B2, c.A1, c.A2);
    }

    private static Biquad Design(ParametricFilter filter, int sampleRate)
    {
        double a = Math.Pow(10, filter.GainDb / 40.0);
        double w0 = 2 * Math.PI * filter.FrequencyHz / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * filter.Q);
        double b0, b1, b2, a0, a1, a2;

        switch (filter.Type)
        {
            case ParametricFilterType.LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case ParametricFilterType.HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            default:
                b0 = 1 + alpha * a;
                b1 = -2 * cos;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cos;
                a2 = 1 - alpha / a;
                break;
        }

        return new Biquad { B0 = b0 / a0, B1 = b1 / a0, B2 = b2 / a0, A1 = a1 / a0, A2 = a2 / a0 };
    }
}
