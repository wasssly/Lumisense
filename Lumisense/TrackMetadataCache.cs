using System.IO;

namespace Lumisense;

// Заголовок и исполнитель из тегов, запомненные на время работы приложения. Окну статистики нужны теги сотен треков, и без кэша
// каждое открытие заново разбирало все файлы; а подготовка трека уже читает теги проигрываемых файлов и сама наполняет кэш.
// Запись действует, пока совпадают размер и время изменения файла.
internal static class TrackMetadataCache
{
    internal sealed record Entry(string? Title, string? Artist, string? AlbumArtist);

    private sealed record Stamp(long LastWriteTicks, long Length, Entry Entry);

    // Предел защищает память при огромной библиотеке: при переполнении кэш просто начинается заново.
    private const int Capacity = 5000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Stamp> Items = new(StringComparer.OrdinalIgnoreCase);

    public static void Remember(string path, string? title, string? artist, string? albumArtist)
    {
        if (!TryStat(path, out long ticks, out long length)) return;

        lock (Gate)
        {
            if (Items.Count >= Capacity && !Items.ContainsKey(path)) Items.Clear();
            Items[path] = new Stamp(ticks, length, new Entry(title, artist, albumArtist));
        }
    }

    public static bool TryGet(string path, out Entry? entry)
    {
        entry = null;
        if (!TryStat(path, out long ticks, out long length)) return false;

        lock (Gate)
        {
            if (!Items.TryGetValue(path, out Stamp? stamp)) return false;
            if (stamp.LastWriteTicks != ticks || stamp.Length != length)
            {
                Items.Remove(path);
                return false;
            }

            entry = stamp.Entry;
            return true;
        }
    }

    // Из кэша, а если записи нет или файл изменился — читает теги и запоминает их. Нечитаемый файл даёт null и в кэш не попадает.
    public static Entry? GetOrRead(string path)
    {
        if (TryGet(path, out Entry? cached)) return cached;

        // ATL не бросает исключение для отсутствующего файла, а возвращает пустой трек с названием из имени файла.
        if (!File.Exists(path)) return null;

        try
        {
            var track = new ATL.Track(path);
            Remember(path, track.Title, track.Artist, track.AlbumArtist);
            return new Entry(track.Title, track.Artist, track.AlbumArtist);
        }
        catch (Exception)
        {
            // Файл мог быть удалён, перемещён или повреждён: вызывающий покажет запасное значение.
            return null;
        }
    }

    internal static void ClearForTests()
    {
        lock (Gate) Items.Clear();
    }

    private static bool TryStat(string path, out long lastWriteTicks, out long length)
    {
        lastWriteTicks = 0;
        length = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;

            lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            length = info.Length;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
