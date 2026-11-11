using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Lumisense;

// Binimum (lyrics-api.binimum.org, «BiniLyrics»): каталог, который по названию и исполнителю отдаёт ссылки на TTML-файлы.
// Сначала запрашивается список совпадений, затем загружаются тексты нескольких лучших; принимаются только https-ссылки.
// Работает только при включённых дополнительных источниках (AppSettings.UseExtraLyricsSource и UseBinimumLyrics):
// название и исполнитель трека уходят на lyrics-api.binimum.org, а загрузка TTML идёт на адрес, который вернул сервис.
internal static class BinimumLyricsSource
{
    public const string SourceName = "Binimum";

    private const string Root = "https://lyrics-api.binimum.org/";
    private const int MaxSongs = 3;
    private static readonly HttpClient Http = ExtraLyricsHttp.CreateClient();

    internal sealed record BiniEntry(string LyricsUrl, string Title, string Artist, string Album, double DurationSeconds);

    public static Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult<IReadOnlyList<OnlineLyricsResult>>(Array.Empty<OnlineLyricsResult>());
        return ExtraLyricsHttp.GuardAsync(SourceName, sourceToken => LoadAsync(title.Trim(), artist, sourceToken), token);
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> LoadAsync(string title, string artist, CancellationToken token)
    {
        string? json = await ExtraLyricsHttp.GetTextAsync(Http, BuildUrl(title, artist), SourceName, token).ConfigureAwait(false);
        if (json is null) return Array.Empty<OnlineLyricsResult>();

        IReadOnlyList<BiniEntry> entries = ParseEntries(json);
        if (entries.Count == 0) return Array.Empty<OnlineLyricsResult>();

        string cleanArtist = ExtraLyricsHttp.CleanArtist(artist) ?? string.Empty;
        Task<OnlineLyricsResult?>[] tasks = entries.Take(MaxSongs)
            .Select(entry => LoadEntryAsync(entry, title, cleanArtist, token)).ToArray();
        OnlineLyricsResult?[] loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
        return loaded.Where(result => result is not null).Select(result => result!).ToList();
    }

    private static async Task<OnlineLyricsResult?> LoadEntryAsync(BiniEntry entry, string title, string artist, CancellationToken token)
    {
        try
        {
            string? ttml = await ExtraLyricsHttp.GetTextAsync(Http, entry.LyricsUrl, SourceName, token).ConfigureAwait(false);
            (string? synced, string? plain) = TtmlLyricsParser.Parse(ttml);
            if (synced is null && plain is null) return null;

            long id = -(5_000_000_000_000L + LyricifyLyricsSource.StableHash(entry.LyricsUrl));
            return new OnlineLyricsResult(
                id,
                entry.Title.Length > 0 ? entry.Title : title,
                entry.Artist.Length > 0 ? entry.Artist : artist,
                entry.Album, entry.DurationSeconds, plain, synced, SourceName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Info($"Текст Binimum не получен: {ex.Message}");
            return null;
        }
    }

    internal static string BuildUrl(string title, string artist)
    {
        var builder = new StringBuilder(Root).Append("?track=").Append(Uri.EscapeDataString(title));
        string? cleanArtist = ExtraLyricsHttp.CleanArtist(artist);
        if (cleanArtist is not null) builder.Append("&artist=").Append(Uri.EscapeDataString(cleanArtist));
        return builder.ToString();
    }

    // Ответ: {"results":[{"lyricsUrl": "...", ...}]}. Поля названия, исполнителя и длительности необязательны.
    internal static IReadOnlyList<BiniEntry> ParseEntries(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
                return Array.Empty<BiniEntry>();

            var entries = new List<BiniEntry>();
            foreach (JsonElement item in results.EnumerateArray())
            {
                string? url = OnlineLyricsJson.GetString(item, "lyricsUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) continue;

                double duration = OnlineLyricsJson.GetDouble(item, "duration");
                // Длительность бывает в миллисекундах: секундой считается всё, что меньше суток.
                if (duration > 86_400) duration /= 1000;

                entries.Add(new BiniEntry(
                    uri.AbsoluteUri,
                    OnlineLyricsJson.GetFirstString(item, "track", "title", "name")?.Trim() ?? string.Empty,
                    OnlineLyricsJson.GetFirstString(item, "artist", "artistName")?.Trim() ?? string.Empty,
                    OnlineLyricsJson.GetFirstString(item, "album", "albumName")?.Trim() ?? string.Empty,
                    duration));
            }

            return entries;
        }
        catch (JsonException)
        {
            return Array.Empty<BiniEntry>();
        }
    }
}
