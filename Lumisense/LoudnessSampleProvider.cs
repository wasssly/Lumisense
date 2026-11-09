using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Тонкомпенсация: чем тише ползунок, тем сильнее полки на 100 Гц и 8 кГц (на малой громкости слух их хуже слышит).
// Коэффициенты считаются вне аудиопотока и подменяются целиком, чтобы горячий путь не блокировался.
public sealed class LoudnessSampleProvider : ISampleProvider
{
    private const double BassHz = 100, TrebleHz = 8000;
    private const double MaxBassDb = 9, MaxTrebleDb = 4;
    private static readonly double HalfGainDb = 20 * Math.Log10(2);

    private sealed record Coefficients(double[] Bass, double[] Treble, bool Active);

    private readonly ISampleProvider _source;
    private readonly double[] _bassState, _trebleState;
    private Coefficients _coefficients = new(new double[5], new double[5], false);
    private bool _stateDirty;
    private int _strengthPercent;
    private double _volumeGain = 1;

    public LoudnessSampleProvider(ISampleProvider source)
    {
        _source = source;
        _bassState = new double[source.WaveFormat.Channels * 4];
        _trebleState = new double[source.WaveFormat.Channels * 4];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    // 0 — выключено, 100 — полная компенсация.
    public int StrengthPercent
    {
        get => Volatile.Read(ref _strengthPercent);
        set
        {
            Volatile.Write(ref _strengthPercent, Math.Clamp(value, 0, 100));
            Recompute();
        }
    }

    // Линейный коэффициент ползунка громкости (1 — максимум).
    public double VolumeGain
    {
        get => Volatile.Read(ref _volumeGain);
        set
        {
            Volatile.Write(ref _volumeGain, Math.Clamp(value, 0, 1));
            Recompute();
        }
    }

    // Подъём в дБ для низов и верхов при текущей громкости; открыт для тестов.
    public static (double BassDb, double TrebleDb) BoostFor(double volumeGain, int strengthPercent)
    {
        double attenuationDb = -20 * Math.Log10(Math.Clamp(volumeGain, 0.001, 1));
        double beyond = Math.Max(0, attenuationDb - HalfGainDb); // порог — половина полной громкости
        double strength = Math.Clamp(strengthPercent, 0, 100) / 100.0;
        return (Math.Min(beyond * 0.35, MaxBassDb) * strength, Math.Min(beyond * 0.15, MaxTrebleDb) * strength);
    }

    private void Recompute()
    {
        var (bassDb, trebleDb) = BoostFor(_volumeGain, _strengthPercent);
        int sampleRate = WaveFormat.SampleRate;
        if (sampleRate < 22050) trebleDb = 0;
        _coefficients = new Coefficients(
            Shelf(bassDb, BassHz, sampleRate, low: true),
            Shelf(trebleDb, TrebleHz, sampleRate, low: false),
            bassDb > 0.01 || trebleDb > 0.01);
    }

    // Полка RBJ (S = 1); массив {b0, b1, b2, a1, a2}, нормированный на a0.
    private static double[] Shelf(double gainDb, double frequency, int sampleRate, bool low)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / 2 * Math.Sqrt(2);
        double root = 2 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        if (low)
        {
            b0 = a * ((a + 1) - (a - 1) * cos + root);
            b1 = 2 * a * ((a - 1) - (a + 1) * cos);
            b2 = a * ((a + 1) - (a - 1) * cos - root);
            a0 = (a + 1) + (a - 1) * cos + root;
            a1 = -2 * ((a - 1) + (a + 1) * cos);
            a2 = (a + 1) + (a - 1) * cos - root;
        }
        else
        {
            b0 = a * ((a + 1) + (a - 1) * cos + root);
            b1 = -2 * a * ((a - 1) + (a + 1) * cos);
            b2 = a * ((a + 1) + (a - 1) * cos - root);
            a0 = (a + 1) - (a - 1) * cos + root;
            a1 = 2 * ((a - 1) - (a + 1) * cos);
            a2 = (a + 1) - (a - 1) * cos - root;
        }

        return [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        var coefficients = Volatile.Read(ref _coefficients);
        if (read <= 0) return read;
        if (!coefficients.Active)
        {
            if (_stateDirty)
            {
                Array.Clear(_bassState);
                Array.Clear(_trebleState);
                _stateDirty = false;
            }

            return read;
        }

        _stateDirty = true;

        int channels = WaveFormat.Channels;
        for (int i = 0; i < read; i++)
        {
            int channel = i % channels;
            double x = buffer[i];
            x = Process(coefficients.Bass, _bassState, channel * 4, x);
            x = Process(coefficients.Treble, _trebleState, channel * 4, x);
            buffer[i] = (float)x;
        }

        return read;
    }

    // Прямая форма I: состояния x1, x2, y1, y2 лежат подряд на канал.
    private static double Process(double[] c, double[] state, int offset, double x)
    {
        double y = c[0] * x + c[1] * state[offset] + c[2] * state[offset + 1] - c[3] * state[offset + 2] - c[4] * state[offset + 3];
        state[offset + 1] = state[offset];
        state[offset] = x;
        state[offset + 3] = state[offset + 2];
        state[offset + 2] = y;
        return y;
    }
}
