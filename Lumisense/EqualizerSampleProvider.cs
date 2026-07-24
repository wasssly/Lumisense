using NAudio.Dsp;
using NAudio.Wave;

namespace AudioPlayer;

/// <summary>
/// Простой N-полосный графический эквалайзер поверх любого ISampleProvider — оборачивает
/// исходный поток воспроизведения (см. MainWindow.LoadAndPlay) и на лету подмешивает peaking
/// EQ фильтры (NAudio.Dsp.BiQuadFilter.PeakingEQ) на классических ISO-частотах графического
/// эквалайзера.
///
/// У каждой полосы — свой набор фильтров НА КАЖДЫЙ КАНАЛ (важно для стерео: у BiQuadFilter
/// есть внутреннее состояние по предыдущим сэмплам, и если гонять оба канала через один и тот
/// же фильтр вперемешку, это состояние "смешается" и исказит звук — поэтому у левого и
/// правого канала свои независимые экземпляры с одинаковыми коэффициентами).
///
/// При Enabled=false сэмплы отдаются как есть, без единого лишнего вычисления — переключатель
/// "Включить эквалайзер" в настройках работает буквально мгновенно и не расходует CPU впустую,
/// когда эквалайзер выключен.
/// </summary>
public sealed class EqualizerSampleProvider : ISampleProvider
{
    // Классические 10 полос графического эквалайзера (стандартные ISO центральные частоты) —
    // тот же набор, что и в большинстве плееров и ресиверов с 10-полосным EQ.
    public static readonly int[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public const double MinGainDb = -12.0;
    public const double MaxGainDb = 12.0;

    // Добротность (Q) фильтра — чем выше, тем уже и "прицельнее" полоса вокруг центральной
    // частоты. 0.9 — типичное значение для графического (не параметрического) эквалайзера,
    // при котором соседние полосы плавно перекрываются, а не звучат отдельными "провалами".
    private const double Bandwidth = 0.9;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter[][] _filters; // [band][channel]
    private readonly double[] _gainsDb;

    public bool Enabled { get; set; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(source.WaveFormat.Channels, 1);

        _gainsDb = new double[BandFrequencies.Length];
        _filters = new BiQuadFilter[BandFrequencies.Length][];

        for (int band = 0; band < BandFrequencies.Length; band++)
        {
            _filters[band] = new BiQuadFilter[_channels];
            for (int channel = 0; channel < _channels; channel++)
                _filters[band][channel] = MakeFilter(band, 0);
        }
    }

    private BiQuadFilter MakeFilter(int band, double gainDb) =>
        BiQuadFilter.PeakingEQ(_source.WaveFormat.SampleRate, BandFrequencies[band], (float)Bandwidth, (float)gainDb);

    /// <summary>Меняет усиление одной полосы (в дБ, обрезается до [-12; 12]) — коэффициенты
    /// фильтра пересчитываются заново для каждого канала, поэтому слайдер в настройках можно
    /// крутить прямо во время воспроизведения, без перезапуска трека.</summary>
    public void SetBandGain(int band, double gainDb)
    {
        if (band < 0 || band >= BandFrequencies.Length) return;

        gainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        _gainsDb[band] = gainDb;

        for (int channel = 0; channel < _channels; channel++)
            _filters[band][channel] = MakeFilter(band, gainDb);
    }

    public double GetBandGain(int band) => band >= 0 && band < _gainsDb.Length ? _gainsDb[band] : 0;

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (!Enabled) return samplesRead;

        for (int n = 0; n < samplesRead; n++)
        {
            int channel = n % _channels;
            float sample = buffer[offset + n];

            for (int band = 0; band < _filters.Length; band++)
                sample = _filters[band][channel].Transform(sample);

            buffer[offset + n] = sample;
        }

        return samplesRead;
    }
}
