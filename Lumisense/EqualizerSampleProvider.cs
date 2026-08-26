using NAudio.Dsp;
using NAudio.Wave;

namespace AudioPlayer;

// N-полосный графический эквалайзер поверх ISampleProvider на BiQuadFilter.PeakingEQ.
// У каждой полосы отдельный фильтр на каждый канал — BiQuadFilter хранит состояние по
// предыдущим сэмплам, гонять оба канала через один и тот же фильтр нельзя, звук исказится.
// При Enabled=false сэмплы отдаются как есть, без единого лишнего вычисления.
public sealed class EqualizerSampleProvider : ISampleProvider
{
    // 10 стандартных ISO-частот, как в большинстве плееров/ресиверов с 10-полосным EQ
    public static readonly int[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public const double MinGainDb = -12.0;
    public const double MaxGainDb = 12.0;

    // Q фильтра — 0.9 типично для графического (не параметрического) EQ: соседние полосы
    // плавно перекрываются, а не звучат отдельными "провалами"
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

    // Гейн одной полосы в дБ, обрезается до [-12; 12]; пересчитывает фильтры на лету,
    // без перезапуска трека
    public void SetBandGain(int band, double gainDb)
    {
        if (band < 0 || band >= BandFrequencies.Length) return;

        gainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        _gainsDb[band] = gainDb;

        for (int channel = 0; channel < _channels; channel++)
            _filters[band][channel] = MakeFilter(band, gainDb);
    }

    public double GetBandGain(int band) => band >= 0 && band < _gainsDb.Length ? _gainsDb[band] : 0;

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (!Enabled) return samplesRead;

        for (int n = 0; n < samplesRead; n++)
        {
            int channel = n % _channels;
            float sample = buffer[n];

            for (int band = 0; band < _filters.Length; band++)
                sample = _filters[band][channel].Transform(sample);

            buffer[n] = sample;
        }

        return samplesRead;
    }
}
