using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Lumisense;

// Неофициальный интерфейс pipe.deezer.com (анонимный токен): любой сбой даёт пустой результат, LRCLIB не страдает.
// Работает только при включённых UseExtraLyricsSource и UseDeezerLyrics: название и исполнитель уходят на серверы Deezer.
internal static class DeezerLyricsSource
{
    public const string SourceName = "Deezer";

    private const string TokenUrl = "https://auth.deezer.com/login/anonymous?jo=p&rto=c";
    private const string SearchUrl = "https://api.deezer.com/2.0/search";
    private const string PipeUrl = "https://pipe.deezer.com/api";
    private const int MaxCandidates = 3;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = ExtraLyricsHttp.CreateClient();
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _token;
    private static DateTime _tokenExpiresUtc;

    // Запрос текста одного трека: простой текст и строки с временем (по строкам и по словам).
    private const string LyricsQuery = """
        query GetLyrics($trackId: String!) {
          track(trackId: $trackId) {
            id
            lyrics {
              id
              text
              synchronizedWordByWordLines { start end words { start end word } }
              synchronizedLines { lrcTimestamp line milliseconds duration }
            }
          }
        }
        """;

    internal sealed record DeezerTrack(long Id, string Title, string Artist, string Album, double Duration);

    public static Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult<IReadOnlyList<OnlineLyricsResult>>(Array.Empty<OnlineLyricsResult>());
        return ExtraLyricsHttp.GuardAsync(SourceName, sourceToken => LoadAsync(title.Trim(), artist, sourceToken), token);
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> LoadAsync(string title, string artist, CancellationToken token)
    {
        string? jwt = await GetTokenAsync(token).ConfigureAwait(false);
        if (jwt is null) return Array.Empty<OnlineLyricsResult>();

        string? searchJson = await ExtraLyricsHttp.GetTextAsync(Http, BuildSearchUrl(title, artist), SourceName, token).ConfigureAwait(false);
        if (searchJson is null) return Array.Empty<OnlineLyricsResult>();

        List<DeezerTrack> tracks = ParseSearch(searchJson).Take(MaxCandidates).ToList();
        if (tracks.Count == 0) return Array.Empty<OnlineLyricsResult>();

        // Несколько лучших совпадений грузятся параллельно; какое из них подходит, решает общий ранжировщик по названию, исполнителю и длительности.
        OnlineLyricsResult?[] loaded = await Task.WhenAll(tracks.Select(track => LoadTrackAsync(jwt, track, token))).ConfigureAwait(false);
        return loaded.OfType<OnlineLyricsResult>().ToList();
    }

    private static async Task<string?> GetTokenAsync(CancellationToken token)
    {
        await TokenLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc) return _token;

            string? json = await ExtraLyricsHttp.GetTextAsync(Http, TokenUrl, SourceName, token).ConfigureAwait(false);
            string? jwt = ParseToken(json);
            if (jwt is null) return null;

            _token = jwt;
            _tokenExpiresUtc = DateTime.UtcNow + TokenLifetime;
            return jwt;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static async Task<OnlineLyricsResult?> LoadTrackAsync(string jwt, DeezerTrack track, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PipeUrl)
            {
                Content = new StringContent(BuildRequestBody(track.Id), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwt}");

            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > ExtraLyricsHttp.MaxResponseCharacters * 4L) return null;

            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (json.Length > ExtraLyricsHttp.MaxResponseCharacters) return null;

            (string? plain, string? synced) = ParseLyrics(json);
            if (plain is null && synced is null) return null;

            return new OnlineLyricsResult(-(7_000_000_000_000L + track.Id), track.Title, track.Artist, track.Album, track.Duration, plain, synced, SourceName);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Logger.Info($"Текст Deezer не получен: {ex.Message}");
            return null;
        }
    }

    internal static string BuildSearchUrl(string title, string artist) =>
        $"{SearchUrl}?q={Uri.EscapeDataString(ExtraLyricsHttp.BuildTitleQuery(title, artist))}&limit=5";

    internal static string BuildRequestBody(long trackId) => JsonSerializer.Serialize(new
    {
        operationName = "GetLyrics",
        variables = new { trackId = trackId.ToString(CultureInfo.InvariantCulture) },
        query = LyricsQuery,
    });

    // Ответ анонимного входа: {"jwt":"..."}.
    internal static string? ParseToken(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            string? jwt = OnlineLyricsJson.GetString(document.RootElement, "jwt");
            return string.IsNullOrWhiteSpace(jwt) ? null : jwt.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Ответ поиска: {"data":[{"id":1,"title":"...","duration":200,"artist":{"name":"..."},"album":{"title":"..."}}]}.
    internal static IReadOnlyList<DeezerTrack> ParseSearch(string json)
    {
        var tracks = new List<DeezerTrack>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return tracks;

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (!long.TryParse(OnlineLyricsJson.GetString(item, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0) continue;

                string title = OnlineLyricsJson.GetString(item, "title")?.Trim() ?? string.Empty;
                if (title.Length == 0) continue;

                string artist = item.TryGetProperty("artist", out JsonElement artistElement) ? OnlineLyricsJson.GetString(artistElement, "name")?.Trim() ?? string.Empty : string.Empty;
                string album = item.TryGetProperty("album", out JsonElement albumElement) ? OnlineLyricsJson.GetString(albumElement, "title")?.Trim() ?? string.Empty : string.Empty;
                tracks.Add(new DeezerTrack(id, title, artist, album, OnlineLyricsJson.GetDouble(item, "duration")));
            }
        }
        catch (JsonException)
        {
            tracks.Clear();
        }

        return tracks;
    }

    // Ответ GraphQL: data.track.lyrics с полями text, synchronizedLines (строка и миллисекунды) и synchronizedWordByWordLines
    // (слова со временем). Построчное время надёжнее, пословное берётся, только если построчного нет.
    internal static (string? Plain, string? Synced) ParseLyrics(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("track", out JsonElement track) || track.ValueKind != JsonValueKind.Object
                || !track.TryGetProperty("lyrics", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
                return (null, null);

            var timed = new List<(double Seconds, string Text)>();
            if (node.TryGetProperty("synchronizedLines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement line in lines.EnumerateArray())
                {
                    string text = OnlineLyricsJson.GetString(line, "line")?.Trim() ?? string.Empty;
                    if (text.Length > 0) timed.Add((OnlineLyricsJson.GetDouble(line, "milliseconds") / 1000.0, text));
                }
            }

            if (timed.Count == 0 && node.TryGetProperty("synchronizedWordByWordLines", out JsonElement wordLines) && wordLines.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement line in wordLines.EnumerateArray())
                {
                    if (!line.TryGetProperty("words", out JsonElement words) || words.ValueKind != JsonValueKind.Array) continue;

                    string text = string.Join(' ', words.EnumerateArray()
                        .Select(word => OnlineLyricsJson.GetString(word, "word")?.Trim() ?? string.Empty)
                        .Where(word => word.Length > 0));
                    if (text.Length > 0) timed.Add((OnlineLyricsJson.GetDouble(line, "start") / 1000.0, text));
                }
            }

            string? synced = timed.Count >= 2 && timed.Select(line => Math.Round(line.Seconds, 2)).Distinct().Count() >= 2
                ? TtmlLyricsParser.BuildLrc(timed)
                : null;

            string? plain = OnlineLyricsJson.GetString(node, "text")?.Trim();
            if (string.IsNullOrWhiteSpace(plain)) plain = timed.Count > 0 ? string.Join('\n', timed.Select(line => line.Text)) : null;

            return (plain, synced);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
