using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Lumisense;

// Теги и обложка трека, прочитанные заранее; ReplayGain считается отдельно, из сохранённого ATL.Track.
internal sealed record TrackTags(
    ATL.Track Track,
    string? Title,
    string? Artist,
    string? Album,
    int BitrateKbps,
    BitmapImage? AlbumArt,
    byte[]? AlbumArtBytes,
    string? AlbumArtMimeType,
    AlbumArtPictureKind? AlbumArtPictureType,
    long TagsMilliseconds,
    long ArtworkMilliseconds);

// Небольшой LRU: ключ — путь + размер + время изменения, поэтому правка файла сбрасывает запись.
internal static class TrackTagCache
{
    private const int Capacity = 8;
    private static readonly object Gate = new();
    private static readonly List<(string Key, TrackTags Tags)> Entries = [];

    // Бросает исключение при ошибке чтения; неудачи не кэшируются.
    public static TrackTags GetOrLoad(string filePath)
    {
        string? key = KeyOf(filePath);
        if (key is not null && TryGet(key, out var cached)) return cached;

        var loaded = Load(filePath);
        if (key is not null) Store(key, loaded);
        return loaded;
    }

    // Фоновое чтение для соседних треков; ошибки гасятся — основной путь прочитает сам.
    public static void Prefetch(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        _ = Task.Run(() =>
        {
            try
            {
                GetOrLoad(filePath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Предзагрузка тегов не удалась для {filePath}: {ex.Message}");
            }
        });
    }

    internal static void Clear()
    {
        lock (Gate) Entries.Clear();
    }

    private static string? KeyOf(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryGet(string key, out TrackTags tags)
    {
        lock (Gate)
        {
            int index = Entries.FindIndex(e => e.Key == key);
            if (index < 0)
            {
                tags = null!;
                return false;
            }

            var entry = Entries[index];
            Entries.RemoveAt(index);
            Entries.Add(entry);
            tags = entry.Tags;
            return true;
        }
    }

    private static void Store(string key, TrackTags tags)
    {
        lock (Gate)
        {
            Entries.RemoveAll(e => e.Key == key);
            Entries.Add((key, tags));
            while (Entries.Count > Capacity) Entries.RemoveAt(0);
        }
    }

    private static TrackTags Load(string filePath)
    {
        var tagsTimer = Stopwatch.StartNew();
        var track = new ATL.Track(filePath);
        string? artist = !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist : track.AlbumArtist;
        TrackMetadataCache.Remember(filePath, track.Title, track.Artist, track.AlbumArtist);
        long tagsMilliseconds = tagsTimer.ElapsedMilliseconds;

        BitmapImage? art = null;
        byte[]? bytes = null;
        string? mime = null;
        AlbumArtPictureKind? pictureType = null;
        long artworkMilliseconds = 0;
        if (track.EmbeddedPictures.Count > 0)
        {
            var artworkTimer = Stopwatch.StartNew();
            try
            {
                var picture = track.EmbeddedPictures[0];
                var pictureBytes = picture.PictureData;
                using var stream = new MemoryStream(pictureBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = TrackPreparationService.ArtworkDisplayDecodePixelWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                bytes = pictureBytes;
                art = bitmap;
                mime = AlbumArtPictureKindExtensions.DetectAlbumArtMimeType(pictureBytes);
                pictureType = AlbumArtPictureKindExtensions.FromAtl(picture.PicType);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Не удалось прочитать обложку файла {filePath}: {ex.Message}");
            }

            artworkMilliseconds = artworkTimer.ElapsedMilliseconds;
        }

        return new TrackTags(track, track.Title, artist, track.Album, Math.Max(0, track.Bitrate), art, bytes, mime, pictureType,
            tagsMilliseconds, artworkMilliseconds);
    }
}
