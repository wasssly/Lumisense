using System;
using System.Threading;
using NAudio.Wave;

namespace Lumisense;

// Сводит каналы в моно, не меняя формат потока: каждый кадр заменяется средним по всем каналам и пишется во все каналы.
// Затем применяет баланс: чем дальше он от центра, тем тише противоположный канал (первые два канала — левый и правый).
// Стоит в цепочке после эквалайзера и работает как переключатель на лету (AppSettings.PlaybackChannelMode), поэтому
// смена режима не требует перезагрузки трека. Для одноканального источника ничего не делает.
public sealed class ChannelModeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private int _monoEnabled;
    private int _balance;

    public ChannelModeSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Пишет UI-поток, читает audio thread: Volatile даёт актуальное значение без блокировок в горячем Read-пути.
    public bool MonoEnabled
    {
        get => Volatile.Read(ref _monoEnabled) != 0;
        set => Volatile.Write(ref _monoEnabled, value ? 1 : 0);
    }

    // От -100 до 100; 0 — центр.
    public int Balance
    {
        get => Volatile.Read(ref _balance);
        set => Volatile.Write(ref _balance, Math.Clamp(value, -100, 100));
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        int channels = WaveFormat.Channels;
        if (read <= 0 || channels < 2) return read;

        int frames = read / channels;
        if (MonoEnabled) MixToMono(buffer, frames, channels);

        int balance = Balance;
        if (balance == 0) return read;

        float leftGain = balance > 0 ? 1f - balance / 100f : 1f;
        float rightGain = balance < 0 ? 1f + balance / 100f : 1f;
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * channels;
            buffer[offset] *= leftGain;
            buffer[offset + 1] *= rightGain;
        }

        return read;
    }

    private static void MixToMono(Span<float> buffer, int frames, int channels)
    {
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
    }
}
