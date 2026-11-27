using System.IO;
using System.Text.Json;

namespace Lumisense;

// Кэш LoudnessAnalyzer; запись привязана к размеру и времени файла, поэтому изменённый файл считается заново.
internal static class LoudnessCache
{
    private sealed class Entry
    {
        public double Lufs { get; set; }
        public long Length { get; set; }
        public long WriteTicks { get; set; }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense", "loudness-cache.json");

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _entries;

    private static Dictionary<string, Entry> Load()
    {
        if (_entries is not null) return _entries;

        try
        {
            _entries = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn($"Не удалось прочитать кэш громкости: {ex.Message}");
            _entries = new();
        }

        return _entries;
    }

    public static bool TryGetLufs(string path, out double lufs)
    {
        lufs = 0;
        try
        {
            var info = new FileInfo(path);
            lock (Gate)
            {
                if (!info.Exists || !Load().TryGetValue(path, out var entry)) return false;
                if (entry.Length != info.Length || entry.WriteTicks != info.LastWriteTimeUtc.Ticks) return false;

                lufs = entry.Lufs;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Store(string path, double lufs)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            lock (Gate)
            {
                var entries = Load();
                entries[path] = new Entry { Lufs = lufs, Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks };
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // Через временный файл: оборванная запись не должна оставить битый кэш.
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries));
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось сохранить кэш громкости: {ex.Message}");
        }
    }
}
