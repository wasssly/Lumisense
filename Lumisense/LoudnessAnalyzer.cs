using NAudio.Wave;

namespace Lumisense;

// Интегральная громкость по ITU-R BS.1770-4 / EBU R128 для выравнивания файлов без ReplayGain-тегов.
public static class LoudnessAnalyzer
{
    private const double ReferenceLufs = -18.0; // опорный уровень ReplayGain 2.0

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public Biquad(double b0, double b1, double b2, double a1, double a2)
        {
            (_b0, _b1, _b2, _a1, _a2) = (b0, b1, b2, a1, a2);
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }

    private static (Biquad Shelf, Biquad HighPass) CreateKWeighting(int sampleRate)
    {
        // Ступень 1: полка +4 дБ от ~1,7 кГц (модель головы).
        const double shelfFrequency = 1681.9744509555319, shelfGainDb = 3.999843853973347, shelfQ = 0.7071752369554196;
        double k = Math.Tan(Math.PI * shelfFrequency / sampleRate);
        double vh = Math.Pow(10, shelfGainDb / 20);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / shelfQ + k * k;
        var shelf = new Biquad(
            (vh + vb * k / shelfQ + k * k) / a0,
            2 * (k * k - vh) / a0,
            (vh - vb * k / shelfQ + k * k) / a0,
            2 * (k * k - 1) / a0,
            (1 - k / shelfQ + k * k) / a0);

        // Ступень 2: фильтр высоких частот (RLB) около 38 Гц.
        const double highPassFrequency = 38.13547087602444, highPassQ = 0.5003270373238773;
        k = Math.Tan(Math.PI * highPassFrequency / sampleRate);
        a0 = 1 + k / highPassQ + k * k;
        var highPass = new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / highPassQ + k * k) / a0);
        return (shelf, highPass);
    }

    // Вес канала: тыловые (5.1, индексы 4 и 5) ×1,41, LFE (индекс 3 при шести каналах) не учитывается.
    private static double ChannelWeight(int channel, int channels) => channels switch
    {
        6 when channel == 3 => 0,
        6 when channel >= 4 => 1.41,
        _ => 1
    };

    // Интегральная громкость в LUFS; −∞, если вся запись тише абсолютного порога. cancel проверяется между буферами.
    public static double MeasureIntegratedLufs(ISampleProvider source, CancellationToken cancel = default)
    {
        int channels = source.WaveFormat.Channels;
        int sampleRate = source.WaveFormat.SampleRate;
        int stepFrames = Math.Max(1, sampleRate / 10);

        var filters = new (Biquad Shelf, Biquad HighPass)[channels];
        for (int channel = 0; channel < channels; channel++) filters[channel] = CreateKWeighting(sampleRate);

        var steps = new List<double>();
        double stepEnergy = 0;
        int stepFilled = 0;
        var buffer = new float[stepFrames * channels];

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            int read = source.Read(buffer);
            if (read <= 0) break;

            int frames = read / channels;
            for (int frame = 0; frame < frames; frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    double weight = ChannelWeight(channel, channels);
                    var (shelf, highPass) = filters[channel];
                    double y = highPass.Process(shelf.Process(buffer[frame * channels + channel]));
                    stepEnergy += weight * y * y;
                }

                if (++stepFilled == stepFrames)
                {
                    steps.Add(stepEnergy / stepFrames);
                    stepEnergy = 0;
                    stepFilled = 0;
                }
            }
        }

        if (stepFilled >= stepFrames / 2) steps.Add(stepEnergy / stepFilled);
        return IntegrateSteps(steps);
    }

    // Блок = четыре подряд идущих шага по 100 мс; запись короче 400 мс считается одним блоком.
    internal static double IntegrateSteps(IReadOnlyList<double> steps)
    {
        if (steps.Count == 0) return double.NegativeInfinity;

        var blocks = new List<double>();
        if (steps.Count < 4)
        {
            blocks.Add(steps.Average());
        }
        else
        {
            for (int i = 0; i + 4 <= steps.Count; i++)
                blocks.Add((steps[i] + steps[i + 1] + steps[i + 2] + steps[i + 3]) / 4);
        }

        static double ToLufs(double energy) => -0.691 + 10 * Math.Log10(energy);

        var aboveAbsolute = blocks.Where(energy => energy > 0 && ToLufs(energy) > -70).ToList();
        if (aboveAbsolute.Count == 0) return double.NegativeInfinity;

        double relativeThreshold = ToLufs(aboveAbsolute.Average()) - 10;
        var gated = aboveAbsolute.Where(energy => ToLufs(energy) > relativeThreshold).ToList();
        return gated.Count == 0 ? double.NegativeInfinity : ToLufs(gated.Average());
    }

    // Линейный множитель, который приводит запись к опорным −18 LUFS (плюс предусилитель); подъём и снижение ограничены разумными пределами.
    public static double GainLinearFor(double lufs, double preampDb)
    {
        if (double.IsNaN(lufs) || double.IsInfinity(lufs)) return 1.0;

        double gainDb = Math.Clamp(ReferenceLufs - lufs, -24, 9) + Math.Clamp(preampDb, -ReplayGainReader.MaxPreampDb, ReplayGainReader.MaxPreampDb);
        return Math.Pow(10, gainDb / 20);
    }
}
