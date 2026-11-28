using System;
using System.IO;

namespace Lumisense;

// Задержка энкодера и добивка из Xing/Info-заголовка LAME: по ним срезается тишина на стыках треков при gapless.
internal readonly record struct Mp3GaplessInfo(int SamplesPerFrame, long FrameCount, int EncoderDelay, int EndPadding)
{
    // Задержка декодера MP3 (528 + 1 отсчёт): декодер не вырезает её сам, формула та же, что у ffmpeg.
    public const int DecoderDelay = 529;

    public static Mp3GaplessInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] data = new byte[16 * 1024];
            return Parse(data.AsSpan(0, stream.Read(data)));
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static Mp3GaplessInfo? Parse(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        if (data.Length >= 10 && data[..3].SequenceEqual("ID3"u8))
        {
            int size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
            offset = 10 + size + ((data[5] & 0x10) != 0 ? 10 : 0);
        }

        // Первый кадр: 11 бит синхронизации.
        while (offset + 4 < data.Length && !(data[offset] == 0xFF && (data[offset + 1] & 0xE0) == 0xE0))
            offset++;
        if (offset + 4 >= data.Length) return null;

        int version = (data[offset + 1] >> 3) & 3;
        int layer = (data[offset + 1] >> 1) & 3;
        if (version == 1 || layer != 1) return null; // нужен Layer III, версия 1 зарезервирована

        bool mpeg1 = version == 3;
        bool mono = ((data[offset + 3] >> 6) & 3) == 3;
        bool crc = (data[offset + 1] & 1) == 0;
        int sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        int tag = offset + 4 + sideInfo + (crc ? 2 : 0);
        if (tag + 8 > data.Length) return null;

        ReadOnlySpan<byte> id = data.Slice(tag, 4);
        if (!id.SequenceEqual("Xing"u8) && !id.SequenceEqual("Info"u8)) return null;

        int flags = data[tag + 7];
        int position = tag + 8;
        if ((flags & 1) == 0 || position + 4 > data.Length) return null; // без числа кадров длину не посчитать
        long frames = (long)((uint)data[position] << 24 | (uint)data[position + 1] << 16 | (uint)data[position + 2] << 8 | data[position + 3]);
        position += 4;
        if ((flags & 2) != 0) position += 4;
        if ((flags & 4) != 0) position += 100;
        if ((flags & 8) != 0) position += 4;

        // Расширение LAME: 21 байт до трёх байт «задержка 12 бит + добивка 12 бит».
        int delayPosition = position + 21;
        if (delayPosition + 3 > data.Length || !data.Slice(position, 4).SequenceEqual("LAME"u8)) return null;

        int delay = (data[delayPosition] << 4) | (data[delayPosition + 1] >> 4);
        int padding = ((data[delayPosition + 1] & 0x0F) << 8) | data[delayPosition + 2];
        return new Mp3GaplessInfo(mpeg1 ? 1152 : 576, frames, delay, padding);
    }
}
