using System;
using System.IO;
using NAudio.Wave;
using NVorbis;

namespace Lumisense;

// Источник звука трека: IEEE float, позиция и длительность; Volume применяется до цепочки (ReplayGain).
public interface ITrackReader : ISampleProvider, IDisposable
{
    string FileName { get; }
    TimeSpan TotalTime { get; }
    TimeSpan CurrentTime { get; set; }
    float Volume { get; set; }
}

// Windows-кодеки через Media Foundation: mp3, wav, wma, flac, m4a, aac и всё остальное, что умеет система.
internal sealed class MediaTrackReader(AudioFileReader inner) : ITrackReader
{
    public MediaTrackReader(string path) : this(new AudioFileReader(path)) { }

    public string FileName => inner.FileName;
    public WaveFormat WaveFormat => inner.WaveFormat;
    public TimeSpan TotalTime => inner.TotalTime;
    public TimeSpan CurrentTime { get => inner.CurrentTime; set => inner.CurrentTime = value; }
    public float Volume { get => inner.Volume; set => inner.Volume = value; }
    public int Read(Span<float> buffer) => inner.Read(buffer);
    public void Dispose() => inner.Dispose();
}

// OGG Vorbis: Media Foundation его не открывает (0xC00D36C4), поэтому декодируем сами через NVorbis.
internal sealed class VorbisTrackReader : ITrackReader
{
    private readonly VorbisReader _reader;
    private float[] _scratch = new float[8192];

    public VorbisTrackReader(string path)
    {
        FileName = path;
        _reader = new VorbisReader(path);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
    }

    public string FileName { get; }
    public WaveFormat WaveFormat { get; }
    public TimeSpan TotalTime => _reader.TotalTime;
    public TimeSpan CurrentTime { get => _reader.TimePosition; set => _reader.TimePosition = value; }
    public float Volume { get; set; } = 1f;

    public int Read(Span<float> buffer)
    {
        if (_scratch.Length < buffer.Length) _scratch = new float[buffer.Length];
        int read = _reader.ReadSamples(_scratch, 0, buffer.Length);
        if (read <= 0) return 0;

        float volume = Volume;
        for (int i = 0; i < read; i++)
            buffer[i] = _scratch[i] * volume;
        return read;
    }

    public void Dispose() => _reader.Dispose();
}

internal static class TrackReaderFactory
{
    public static ITrackReader Open(string path)
    {
        if (IsOggVorbis(path))
        {
            try
            {
                return new VorbisTrackReader(path);
            }
            catch (Exception ex)
            {
                // Не Vorbis (например, Opus в Ogg) или файл повреждён: пробуем системный декодер, как раньше.
                Logger.Warn($"NVorbis не открыл {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new MediaTrackReader(path);
    }

    // Контейнер Ogg: страница начинается с «OggS», а Vorbis помечен «\x01vorbis» в первом пакете.
    internal static bool IsOggVorbis(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".oga", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[64];
            int read = stream.Read(head);
            ReadOnlySpan<byte> bytes = head[..read];
            return bytes.StartsWith("OggS"u8) && bytes.IndexOf("\u0001vorbis"u8) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
