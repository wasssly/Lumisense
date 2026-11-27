using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Lumisense;

// LRCMUX (lrcmux.dev): сводный сервис с интерфейсом, совместимым с LRCLIB (/get и /search с полями trackName, artistName,
// plainLyrics, syncedLyrics). Ответ разбирается так же, как ответ LRCLIB. Основной адрес опрашивается первым, запасной — если он
// не ответил. Работает только при включённых UseExtraLyricsSource и UseLrcMuxLyrics: название и исполнитель уходят на сервер сервиса.
internal static class LrcMuxLyricsSource
{
    public const string SourceName = "LRCMUX";

    internal static readonly string[] Servers =
    {
        "https://lrcmux.dev/api",
        "https://api.lrcmux.dev/api",
    };

    private const int MaxResults = 5;

    private static readonly HttpClient Http = ExtraLyricsHttp.CreateClient();

    public static Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult<IReadOnlyList<OnlineLyricsResult>>(Array.Empty<OnlineLyricsResult>());
        return ExtraLyricsHttp.GuardAsync(SourceName, sourceToken => LoadAsync(title.Trim(), artist, sourceToken), token);
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> LoadAsync(string title, string artist, CancellationToken token)
    {
        foreach (string server in Servers)
        {
            string? json;
            try
            {
                json = await ExtraLyricsHttp.GetTextAsync(Http, BuildUrl(server, title, artist), SourceName, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                Logger.Info($"LRCMUX ({server}) не ответил: {ex.Message}");
                continue;
            }

            if (json is null) continue;

            IReadOnlyList<OnlineLyricsResult> results = ParseResponse(json);
            if (results.Count > 0) return results;
        }

        return Array.Empty<OnlineLyricsResult>();
    }

    internal static string BuildUrl(string server, string title, string artist)
    {
        var builder = new StringBuilder(server.TrimEnd('/')).Append("/get?track_name=").Append(Uri.EscapeDataString(title));
        string? cleanArtist = ExtraLyricsHttp.CleanArtist(artist);
        if (cleanArtist is not null) builder.Append("&artist_name=").Append(Uri.EscapeDataString(cleanArtist));
        return builder.ToString();
    }

    // Ответ: объект (как /get) или массив объектов (как /search); инструментальные треки и записи без текста пропускаются.
    internal static IReadOnlyList<OnlineLyricsResult> ParseResponse(string json)
    {
        var results = new List<OnlineLyricsResult>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind switch
            {
                JsonValueKind.Object => new[] { root },
                JsonValueKind.Array => root.EnumerateArray().Take(MaxResults).ToList(),
                _ => Array.Empty<JsonElement>(),
            };

            foreach (JsonElement item in items)
            {
                if (item.ValueKind != JsonValueKind.Object || OnlineLyricsJson.GetBool(item, "instrumental")) continue;

                string? plain = OnlineLyricsJson.GetString(item, "plainLyrics");
                string? synced = OnlineLyricsJson.GetString(item, "syncedLyrics");
                if (string.IsNullOrWhiteSpace(plain) && string.IsNullOrWhiteSpace(synced)) continue;

                string title = OnlineLyricsJson.GetString(item, "trackName")?.Trim() is { Length: > 0 } name ? name : OnlineLyricsJson.GetString(item, "name")?.Trim() ?? string.Empty;
                if (title.Length == 0) continue;

                string artist = OnlineLyricsJson.GetString(item, "artistName")?.Trim() ?? string.Empty;
                string album = OnlineLyricsJson.GetString(item, "albumName")?.Trim() ?? string.Empty;
                long sourceId = long.TryParse(OnlineLyricsJson.GetString(item, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out long parsedId) && parsedId > 0
                    ? parsedId
                    : LyricifyLyricsSource.StableHash($"{title}|{artist}");

                results.Add(new OnlineLyricsResult(-(8_000_000_000_000L + sourceId), title, artist, album,
                    OnlineLyricsJson.GetDouble(item, "duration"), string.IsNullOrWhiteSpace(plain) ? null : plain, string.IsNullOrWhiteSpace(synced) ? null : synced, SourceName));
            }
        }
        catch (JsonException)
        {
            results.Clear();
        }

        return results;
    }
}
