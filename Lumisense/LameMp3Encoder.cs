using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Lumisense;

// Кодирует PCM в MP3 через нативную libmp3lame (LAME) вместо Media Foundation Windows — не
// зависит от того, установлен ли в системе кодировщик MP3 (см. TrackExportService и разбор
// ошибки "Was not able to create a sink writer for this file extension": Media Foundation
// на части систем — не только N/KN редакции — этого кодировщика вообще не содержит).
//
// LAME распространяется под LGPL — P/Invoke к отдельной libmp3lame.dll является динамической
// линковкой, что LGPL прямо разрешает без каких-либо ограничений на лицензию самого Lumisense.
// Саму DLL этот класс не поставляет и не может: libmp3lame.dll (Win64) нужно положить в папку
// проекта отдельно — самый воспроизводимый и доверенный способ получить официальную сборку:
// `vcpkg install lame:x64-windows`, оттуда скопировать installed\x64-windows\bin\libmp3lame.dll.
// Если DLL нет — используется TryEncode, а не Encode: вызывающий код (TrackExportService)
// сам решает, что делать при неудаче (например, попробовать Media Foundation как запасной
// вариант), а не получает необработанное исключение из P/Invoke.
public static class LameMp3Encoder
{
    private const string LibraryName = "libmp3lame";

    // Коды из lame.h: 0 — успех, отрицательные — конкретная ошибка (переполнение буфера,
    // нехватка памяти, lame_init_params не вызван и т.п.). Здесь достаточно различать "успех"
    // и "не успех" — TryEncode не пытается диагностировать LAME точнее самой LAME.
    private const int Stereo = 0;
    private const int JointStereo = 1;
    private const int Mono = 3;

    // Общепринятое компромиссное значение качества/скорости среди обёрток над LAME: 0 — лучшее
    // и самое медленное, 9 — худшее и самое быстрое. 2 практически неотличимо от 0 на слух,
    // но заметно быстрее.
    private const int QualityGoodFast = 2;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lame_init();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_set_num_channels(IntPtr gfp, int channels);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_set_in_samplerate(IntPtr gfp, int sampleRate);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_set_brate(IntPtr gfp, int kilobitsPerSecond);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_set_mode(IntPtr gfp, int mode);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_set_quality(IntPtr gfp, int quality);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_init_params(IntPtr gfp);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_encode_buffer(
        IntPtr gfp, short[] pcmLeft, short[]? pcmRight, int numSamplesPerChannel,
        byte[] mp3Buffer, int mp3BufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_encode_flush(IntPtr gfp, byte[] mp3Buffer, int mp3BufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_close(IntPtr gfp);

    // source должен быть уже в формате 16-bit PCM с частотой и числом каналов, которые LAME
    // принимает как есть (см. TrackExportService.CreateMp3CompatibleProvider — та же
    // подготовка, что раньше шла в MediaFoundationEncoder). destinationPath — конечный файл,
    // не временный: отдельного шага File.Move здесь нет, это решает вызывающий код.
    //
    // Возвращает false при любой проблеме (нет DLL, версия не подошла, LAME вернула ошибку) —
    // не бросает исключение, чтобы вызывающий код мог спокойно попробовать запасной вариант.
    public static bool TryEncode(IWaveProvider source, string destinationPath, int bitRateBitsPerSecond)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || source.WaveFormat.BitsPerSample != 16)
            return false;

        IntPtr gfp = IntPtr.Zero;
        FileStream? output = null;
        try
        {
            gfp = lame_init();
            if (gfp == IntPtr.Zero) return false;

            int channels = source.WaveFormat.Channels;
            lame_set_num_channels(gfp, channels);
            lame_set_in_samplerate(gfp, source.WaveFormat.SampleRate);
            lame_set_brate(gfp, Math.Max(1, bitRateBitsPerSecond / 1000));
            lame_set_mode(gfp, channels == 1 ? Mono : JointStereo);
            lame_set_quality(gfp, QualityGoodFast);

            if (lame_init_params(gfp) < 0) return false;

            output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);

            const int samplesPerChunk = 8192; // сэмплов НА КАНАЛ за один вызов lame_encode_buffer
            var readBuffer = new byte[samplesPerChunk * channels * sizeof(short)];
            var left = new short[samplesPerChunk];
            var right = channels == 2 ? new short[samplesPerChunk] : null;
            // 1.25×вход + 7200 байт — стандартный запас с достаточным запасом, рекомендованный
            // в lame.h для mp3buf_size у lame_encode_buffer/lame_encode_flush.
            var mp3Buffer = new byte[(int)(samplesPerChunk * 1.25) + 7200];

            int bytesRead;
            while ((bytesRead = source.Read(readBuffer)) > 0)
            {
                int bytesPerFrame = channels * sizeof(short);
                int frames = bytesRead / bytesPerFrame;
                DeinterleavePcm16(readBuffer, frames, channels, left, right);

                int encoded = lame_encode_buffer(gfp, left, right, frames, mp3Buffer, mp3Buffer.Length);
                if (encoded < 0) return false;
                if (encoded > 0) output.Write(mp3Buffer, 0, encoded);
            }

            int flushed = lame_encode_flush(gfp, mp3Buffer, mp3Buffer.Length);
            if (flushed < 0) return false;
            if (flushed > 0) output.Write(mp3Buffer, 0, flushed);

            return true;
        }
        catch (DllNotFoundException)
        {
            // libmp3lame.dll не положена рядом с приложением — ожидаемый, не аварийный случай
            // (см. комментарий класса), не BadImageFormatException и не что-то ещё неожиданное.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // DLL есть, но это не та версия LAME (нет одной из ожидаемых функций) — тоже просто
            // "не получилось", а не повод ронять экспорт целиком.
            return false;
        }
        finally
        {
            output?.Dispose();
            if (gfp != IntPtr.Zero) lame_close(gfp);
        }
    }

    private static void DeinterleavePcm16(byte[] source, int frames, int channels, short[] left, short[]? right)
    {
        for (int i = 0; i < frames; i++)
        {
            int baseIndex = i * channels * sizeof(short);
            left[i] = BitConverter.ToInt16(source, baseIndex);
            if (right != null)
                right[i] = BitConverter.ToInt16(source, baseIndex + sizeof(short));
        }
    }
}
