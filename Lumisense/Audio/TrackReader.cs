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
// У mp3 с заголовком LAME срезает задержку энкодера и добивку, чтобы на стыках не было пауз.
internal sealed class MediaTrackReader : ITrackReader
{
    private readonly AudioFileReader _inner;
    private readonly int _channels;
    private readonly long _skipFrames;
    private readonly long _totalFrames;
    private long _delivered;
    private bool _skipPending;

    public MediaTrackReader(AudioFileReader inner)
    {
        _inner = inner;
        _channels = Math.Max(1, inner.WaveFormat.Channels);
    }

    public MediaTrackReader(string path) : this(new AudioFileReader(path))
    {
        if (!Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || Mp3GaplessInfo.TryRead(path) is not { } info)
            return;

        long raw = info.FrameCount * info.SamplesPerFrame;
        long total = raw - info.EncoderDelay - info.EndPadding;
        long innerFrames = (long)(_inner.TotalTime.TotalSeconds * _inner.WaveFormat.SampleRate);

        // Если длина декодера уже не сходится с числом кадров, декодер сам что-то срезал: ничего не трогаем.
        bool plausible = info.EncoderDelay <= 4000 && info.EndPadding <= 4000 && total > 0
            && Math.Abs(innerFrames - raw) <= 2L * info.SamplesPerFrame;
        if (!plausible) return;

        _skipFrames = info.EncoderDelay + Mp3GaplessInfo.DecoderDelay;
        _totalFrames = total;
        _skipPending = true;
    }

    public string FileName => _inner.FileName;
    public WaveFormat WaveFormat => _inner.WaveFormat;
    public float Volume { get => _inner.Volume; set => _inner.Volume = value; }

    private bool Trimmed => _totalFrames > 0;

    public TimeSpan TotalTime => Trimmed ? TimeSpan.FromSeconds(_totalFrames / (double)WaveFormat.SampleRate) : _inner.TotalTime;

    public TimeSpan CurrentTime
    {
        get => Trimmed ? TimeSpan.FromSeconds(_delivered / (double)WaveFormat.SampleRate) : _inner.CurrentTime;
        set
        {
            if (!Trimmed)
            {
                _inner.CurrentTime = value;
                return;
            }

            // В начало возвращаемся заново и снова пропускаем задержку энкодера; в остальные места — со смещением.
            bool toStart = value < TimeSpan.FromMilliseconds(1);
            _inner.CurrentTime = toStart ? TimeSpan.Zero : value + TimeSpan.FromSeconds(_skipFrames / (double)WaveFormat.SampleRate);
            _skipPending = toStart;
            _delivered = toStart ? 0 : (long)(value.TotalSeconds * WaveFormat.SampleRate);
        }
    }

    public int Read(Span<float> buffer)
    {
        if (!Trimmed) return _inner.Read(buffer);

        if (_skipPending)
        {
            DiscardFrames(_skipFrames);
            _skipPending = false;
        }

        long left = (_totalFrames - _delivered) * _channels;
        if (left <= 0) return 0;

        int read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        _delivered += read / _channels;
        return read;
    }

    private void DiscardFrames(long frames)
    {
        float[] scratch = new float[4096 * _channels];
        long remaining = frames * _channels;
        while (remaining > 0)
        {
            int read = _inner.Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, remaining)));
            if (read <= 0) break;
            remaining -= read;
        }
    }

    public void Dispose() => _inner.Dispose();
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
        try
        {
            if (IsOggVorbis(path)) return new VorbisTrackReader(path);
            if (IsOggOpus(path)) return new OpusTrackReader(path);
        }
        catch (Exception ex)
        {
            // Нестандартный или повреждённый файл: пробуем системный декодер, как раньше.
            Logger.Warn($"Управляемый декодер не открыл {Path.GetFileName(path)}: {ex.Message}");
        }

        return new MediaTrackReader(path);
    }

    internal static bool IsOggVorbis(string path) => OggCodecMarker(path, "\u0001vorbis"u8);

    internal static bool IsOggOpus(string path) => OggCodecMarker(path, "OpusHead"u8);

    // Контейнер Ogg: первая страница начинается с «OggS», тип кодека написан в самом первом пакете.
    private static bool OggCodecMarker(string path, ReadOnlySpan<byte> marker)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".oga", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".opus", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[64];
            int read = stream.Read(head);
            ReadOnlySpan<byte> bytes = head[..read];
            return bytes.StartsWith("OggS"u8) && bytes.IndexOf(marker) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
