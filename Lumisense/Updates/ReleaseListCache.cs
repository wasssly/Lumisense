using System.IO;
using System.Text.Json;

namespace Lumisense;

internal sealed class ReleaseListCacheData
{
    public string? ETag { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public List<ReleaseListItem> Releases { get; set; } = new();
}

// Последний удачно полученный список релизов для страницы «Все версии» (%AppData%\Lumisense\releases-cache.json). Нужен для
// трёх вещей: показать список, когда GitHub API отвечает 403/429 (лимит запросов без токена — 60 в час на адрес) или недоступен;
// условный запрос по ETag (ответ 304 в лимит GitHub не засчитывается); мгновенный повтор без обращения к сети.
internal static class ReleaseListCache
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense", "releases-cache.json");

    public static string Serialize(ReleaseListCacheData data) => JsonSerializer.Serialize(data);

    public static ReleaseListCacheData? Deserialize(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<ReleaseListCacheData>(json);
            return data is { Releases.Count: > 0 } ? data : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ReleaseListCacheData? TryLoad(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            return File.Exists(file) ? Deserialize(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось прочитать кэш списка версий: {ex.Message}");
            return null;
        }
    }

    public static void TrySave(ReleaseListCacheData data, string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // Через временный файл: оборванная запись не должна оставить битый кэш.
            string temp = file + ".tmp";
            File.WriteAllText(temp, Serialize(data));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось сохранить кэш списка версий: {ex.Message}");
        }
    }
}
