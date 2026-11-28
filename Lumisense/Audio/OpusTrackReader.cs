using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Concentus;
using NAudio.Wave;

namespace Lumisense;

// Ogg Opus (RFC 7845): Media Foundation его не открывает, поэтому Ogg-страницы разбираем сами, а пакеты декодирует Concentus.
// Поддерживаются моно и стерео (mapping family 0) и один логический поток в файле.
internal sealed class OpusTrackReader : ITrackReader
{
    private const int SampleRate = 48000;
    private const int MaxFrameSize = 5760;
    private const int SeekPreRoll = 3840;

    private readonly record struct PageEntry(long Offset, long Granule, bool Continued);

    private readonly FileStream _stream;
    private readonly IOpusDecoder _decoder;
    private readonly List<PageEntry> _pages = [];
    private readonly Queue<byte[]> _packets = new();
    private readonly MemoryStream _partial = new();
    private readonly float[] _pcm;
    private readonly object _gate = new();
    private readonly int _channels;
    private readonly int _preSkip;
    private readonly float _headerGain;
    private readonly long _totalFrames;
    private readonly int _audioFirstPage;

    private int _nextPage;
    private bool _skipFirstPacket;
    private int _pcmPos;
    private int _pcmCount;
    private long _discard;
    private long _position;

    public OpusTrackReader(string path)
    {
        FileName = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            ScanPages();
            if (_pages.Count < 3) throw new InvalidDataException("В файле нет аудиоданных Opus.");

            byte[] head = NextPacket() ?? throw new InvalidDataException("Нет заголовка OpusHead.");
            if (head.Length < 19 || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8) || head[8] >> 4 != 0)
                throw new InvalidDataException("Это не поток Opus.");
            _channels = head[9];
            if (_channels is < 1 or > 2 || head[18] != 0)
                throw new NotSupportedException("Поддерживается только моно и стерео Opus.");
            _preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
            _headerGain = (float)Math.Pow(10, BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16)) / 256.0 / 20.0);
            _ = NextPacket() ?? throw new InvalidDataException("Нет заголовка OpusTags.");
            _audioFirstPage = _nextPage;

            long lastGranule = 0;
            foreach (PageEntry page in _pages)
                if (page.Granule > lastGranule) lastGranule = page.Granule;
            _totalFrames = Math.Max(0, lastGranule - _preSkip);

            _decoder = OpusCodecFactory.CreateDecoder(SampleRate, _channels);
            _pcm = new float[MaxFrameSize * _channels];
            _discard = _preSkip;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, _channels);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string FileName { get; }

    public WaveFormat WaveFormat { get; }

    public TimeSpan TotalTime => TimeSpan.FromSeconds(_totalFrames / (double)SampleRate);

    public float Volume { get; set; } = 1f;

    public TimeSpan CurrentTime
    {
        get { lock (_gate) return TimeSpan.FromSeconds(_position / (double)SampleRate); }
        set => Seek(value);
    }

    public int Read(Span<float> buffer)
    {
        lock (_gate)
        {
            int written = 0;
            float gain = _headerGain * Volume;
            while (written < buffer.Length)
            {
                long remaining = (_totalFrames - _position) * _channels;
                if (remaining <= 0) break;

                if (_pcmPos >= _pcmCount && !DecodeNext()) break;

                int count = (int)Math.Min(Math.Min(buffer.Length - written, _pcmCount - _pcmPos), remaining);
                for (int i = 0; i < count; i++)
                    buffer[written + i] = _pcm[_pcmPos + i] * gain;

                _pcmPos += count;
                written += count;
                _position += count / _channels;
            }

            return written;
        }
    }

    public void Dispose() => _stream.Dispose();

    // Декодирует следующий пакет в _pcm; false — поток закончился.
    private bool DecodeNext()
    {
        while (true)
        {
            byte[]? packet = NextPacket();
            if (packet is null) return false;

            int frames;
            try
            {
                frames = _decoder.Decode(packet, _pcm, MaxFrameSize);
            }
            catch (Exception ex)
            {
                // Битый пакет пропускаем: лучше короткая тишина, чем остановка трека.
                Logger.Warn($"Opus: пакет пропущен: {ex.Message}");
                continue;
            }

            if (frames <= 0) continue;

            int drop = (int)Math.Min(_discard, frames);
            _discard -= drop;
            _pcmPos = drop * _channels;
            _pcmCount = frames * _channels;
            if (_pcmPos < _pcmCount) return true;
        }
    }

    private void Seek(TimeSpan time)
    {
        lock (_gate)
        {
            long target = (long)(Math.Clamp(time.TotalSeconds, 0, _totalFrames / (double)SampleRate) * SampleRate);
            long absolute = target + _preSkip;
            long warmUp = Math.Max(0, absolute - SeekPreRoll);

            // Якорь — страница, после которой начнём декодировать: её granule считает всё до её последнего целого пакета.
            int anchor = -1;
            for (int i = _audioFirstPage; i < _pages.Count; i++)
            {
                if (_pages[i].Granule < 0) continue;
                if (_pages[i].Granule > warmUp) break;
                anchor = i;
            }

            // Страница после якоря не должна начинаться с хвоста чужого пакета: иначе сдвинется отсчёт кадров.
            while (anchor >= 0 && anchor + 1 < _pages.Count && (_pages[anchor + 1].Continued || _pages[anchor].Granule < 0))
                anchor++;

            _packets.Clear();
            _partial.SetLength(0);
            _skipFirstPacket = false;
            _decoder.ResetState();
            _pcmPos = _pcmCount = 0;
            _position = target;

            if (anchor < 0)
            {
                _nextPage = _audioFirstPage;
                _discard = absolute;
            }
            else
            {
                _nextPage = anchor + 1;
                _discard = Math.Max(0, absolute - _pages[anchor].Granule);
            }
        }
    }

    private byte[]? NextPacket()
    {
        while (_packets.Count == 0)
        {
            if (_nextPage >= _pages.Count) return null;
            LoadPage(_pages[_nextPage++]);
        }

        return _packets.Dequeue();
    }

    private void LoadPage(PageEntry page)
    {
        _stream.Position = page.Offset;
        byte[] header = new byte[27];
        _stream.ReadExactly(header);
        byte[] table = new byte[header[26]];
        _stream.ReadExactly(table);
        int bodyLength = 0;
        foreach (byte lace in table) bodyLength += lace;
        byte[] body = new byte[bodyLength];
        _stream.ReadExactly(body);

        int position = 0;
        foreach (byte lace in table)
        {
            _partial.Write(body, position, lace);
            position += lace;
            if (lace == 255) continue;

            if (_skipFirstPacket) _skipFirstPacket = false;
            else _packets.Enqueue(_partial.ToArray());
            _partial.SetLength(0);
        }
    }

    // Индекс страниц одного логического потока: смещение и granule нужны для длительности и перемотки.
    private void ScanPages()
    {
        Span<byte> header = stackalloc byte[27];
        byte[] table = new byte[255];
        long offset = 0;
        int? serial = null;
        while (true)
        {
            _stream.Position = offset;
            if (_stream.Read(header) < 27 || !header[..4].SequenceEqual("OggS"u8)) break;

            int segments = header[26];
            if (_stream.Read(table, 0, segments) < segments) break;
            int bodyLength = 0;
            for (int i = 0; i < segments; i++) bodyLength += table[i];

            int pageSerial = BinaryPrimitives.ReadInt32LittleEndian(header[14..]);
            bool continued = (header[5] & 1) != 0;
            bool begin = (header[5] & 2) != 0;
            long next = offset + 27 + segments + bodyLength;

            if (serial is null) serial = pageSerial;
            else if (pageSerial != serial)
            {
                // Новый логический поток (цепочка) — берём только первый.
                if (begin) break;
                offset = next;
                continue;
            }

            _pages.Add(new PageEntry(offset, BinaryPrimitives.ReadInt64LittleEndian(header[6..]), continued));
            offset = next;
        }
    }
}
