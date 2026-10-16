using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Сводит каналы в моно, не меняя формат потока: каждый кадр заменяется средним по всем каналам и пишется во все каналы.
// Стоит в цепочке после эквалайзера и работает как переключатель на лету (AppSettings.PlaybackChannelMode), поэтому
// смена режима не требует перезагрузки трека. Для одноканального источника ничего не делает.
public sealed class ChannelModeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private int _monoEnabled;

    public ChannelModeSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Пишет UI-поток, читает audio thread: Volatile даёт актуальное значение без блокировок в горячем Read-пути.
    public bool MonoEnabled
    {
        get => Volatile.Read(ref _monoEnabled) != 0;
        set => Volatile.Write(ref _monoEnabled, value ? 1 : 0);
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        int channels = WaveFormat.Channels;
        if (read <= 0 || channels < 2 || !MonoEnabled) return read;

        int frames = read / channels;
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * channels;
            float sum = 0f;
            for (int channel = 0; channel < channels; channel++)
                sum += buffer[offset + channel];

            float mono = sum / channels;
            for (int channel = 0; channel < channels; channel++)
                buffer[offset + channel] = mono;
        }

        return read;
    }
}
