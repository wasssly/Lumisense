namespace Lumisense;

// Переводит мощность полос спектра в высоту 0..1: каждая полоса сравнивается с собственным медленным «базовым» уровнем,
// поэтому полоски шевелятся и на тихой, и на громкой музыке, а не упираются в потолок.
internal sealed class BandMeter
{
    private const double RangeDb = 10, RestingHeight = 0.5, SpreadDb = 12, FloorDb = -70;
    private const double BaselineRiseSeconds = 0.8, BaselineSinkSeconds = 2.5;

    private readonly double[] _db, _baseline;

    public BandMeter(int bars)
    {
        Heights = new double[bars];
        _db = new double[bars];
        _baseline = new double[bars];
        Array.Fill(_baseline, FloorDb);
    }

    public double[] Heights { get; }

    public void Measure(float[] spectrum, double dt)
    {
        int bars = Heights.Length;
        double headroom = RangeDb * (1 - RestingHeight), top = FloorDb;
        for (int i = 0; i < bars; i++)
        {
            int from = i * spectrum.Length / bars, to = Math.Max((i + 1) * spectrum.Length / bars, from + 1);
            double power = 0;
            for (int bin = from; bin < to; bin++) power += spectrum[bin];
            double db = _db[i] = 10 * Math.Log10(power / (to - from) + 1e-14);

            if (db > FloorDb)
            {
                double baseline = _baseline[i];
                baseline += (db - baseline) * (1 - Math.Exp(-dt / (db > baseline ? BaselineRiseSeconds : BaselineSinkSeconds)));
                _baseline[i] = Math.Max(baseline, db - headroom);
            }

            top = Math.Max(top, _baseline[i]);
        }

        for (int i = 0; i < bars; i++)
        {
            double reference = Math.Max(_baseline[i], top - SpreadDb);
            Heights[i] = Math.Clamp(RestingHeight + (_db[i] - reference) / RangeDb, 0, 1);
        }
    }
}
