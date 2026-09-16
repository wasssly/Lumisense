using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Lumisense;

// Кодирует PCM в MP3 через libmp3lame вместо Media Foundation, которое на части систем не
// содержит MP3-кодировщика. LAME — LGPL; см. lib/THIRD-PARTY-NOTICES.md.
public static class LameMp3Encoder
{
    private const string LibraryName = "libmp3lame";

    // Коды из lame.h.
    private const int Stereo = 0;
    private const int JointStereo = 1;
    private const int Mono = 3;

    // 0 — лучшее и самое медленное качество, 9 — худшее и самое быстрое; 2 почти неотличимо от 0.
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

    // source должен уже быть 16-bit PCM (см. TrackExportService.CreateMp3CompatibleProvider).
    // Возвращает false вместо бросания исключения — вызывающий код сам решает про запасной вариант.
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
            // 1.25×вход + 7200 байт — запас, рекомендованный в lame.h для mp3buf_size.
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
            // libmp3lame.dll не положена рядом с приложением — ожидаемый случай, не аварийный.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // DLL есть, но не та версия LAME.
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
