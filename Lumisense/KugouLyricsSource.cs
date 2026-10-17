using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumisense;

// Kugou Music: поиск песни по названию и исполнителю (mobilecdn.kugou.com), затем текст в LRC по хешу песни (lyrics.kugou.com).
// Работает только при включённой настройке дополнительных источников (AppSettings.UseExtraLyricsSource); название и
// исполнитель трека уходят на серверы Kugou. Сбой или медленный ответ источника превращается в пустой список,
// чтобы не мешать остальным источникам.
internal static class KugouLyricsSource
{
    public const string SourceName = "Kugou";

    private const int MaxSongs = 4;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(7);
    private static readonly Regex TimestampRegex = new(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);
    // Служебные теги Kugou ([id:$00000000], [ar:], [ti:], [by:] и т. п.) в текст попадать не должны; [offset:] парсер LRC учитывает.
    private static readonly Regex HeaderTagRegex = new(@"^\[(?:id|ar|ti|al|by|hash|sign|qq|total|re|ve|length):[^\]]*\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly char[] ArtistSeparators = { ';', ',' };
    private static readonly HttpClient Http = CreateHttpClient();

    internal sealed record KugouSong(string Hash, string Title, string Artist, string Album, double DurationSeconds);

    public static async Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<OnlineLyricsResult>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SourceTimeout);
        CancellationToken sourceToken = timeout.Token;

        try
        {
            string cleanTitle = title.Trim();
            string mainArtist = string.IsNullOrWhiteSpace(artist) || artist == "—"
                ? string.Empty
                : artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

            IReadOnlyList<KugouSong> songs = await SearchSongsAsync(mainArtist.Length == 0 ? cleanTitle : $"{mainArtist} {cleanTitle}", sourceToken)
                .ConfigureAwait(false);
            if (songs.Count == 0 && mainArtist.Length > 0)
                songs = await SearchSongsAsync(cleanTitle, sourceToken).ConfigureAwait(false);
            if (songs.Count == 0) return Array.Empty<OnlineLyricsResult>();

            Task<OnlineLyricsResult?>[] tasks = songs.Take(MaxSongs).Select(song => LoadAsync(song, sourceToken)).ToArray();
            OnlineLyricsResult?[] loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
            return loaded.Where(result => result is not null).Select(result => result!).ToList();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Logger.Info($"Источник текстов {SourceName} не ответил за {SourceTimeout.TotalSeconds:0} с");
            return Array.Empty<OnlineLyricsResult>();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Источник текстов {SourceName} недоступен: {ex.Message}");
            return Array.Empty<OnlineLyricsResult>();
        }
    }

    private static async Task<IReadOnlyList<KugouSong>> SearchSongsAsync(string keyword, CancellationToken token)
    {
        string json = await GetStringAsync(
            $"mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize=10&showtype=1",
            token).ConfigureAwait(false);
        return ParseSongs(json);
    }

    private static async Task<OnlineLyricsResult?> LoadAsync(KugouSong song, CancellationToken token)
    {
        try
        {
            string keyword = string.IsNullOrWhiteSpace(song.Artist) ? song.Title : $"{song.Artist} - {song.Title}";
            long milliseconds = (long)Math.Round(song.DurationSeconds * 1000);
            string search = await GetStringAsync(
                $"lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(keyword)}" +
                $"&hash={Uri.EscapeDataString(song.Hash)}&timelength={milliseconds}",
                token).ConfigureAwait(false);

            (string Id, string AccessKey)? candidate = ParseCandidate(search);
            if (candidate is null) return null;

            string download = await GetStringAsync(
                $"lyrics.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(candidate.Value.Id)}" +
                $"&accesskey={Uri.EscapeDataString(candidate.Value.AccessKey)}&fmt=lrc&charset=utf8",
                token).ConfigureAwait(false);

            string? text = DecodeContent(download);
            if (string.IsNullOrWhiteSpace(text)) return null;

            bool synced = TimestampRegex.IsMatch(text);
            long id = -(2_000_000_000_000L + LyricifyLyricsSource.StableHash(song.Hash));
            return new OnlineLyricsResult(id, song.Title, song.Artist, song.Album, song.DurationSeconds,
                synced ? null : text, synced ? text : null, SourceName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Info($"Текст Kugou не получен ({song.Hash}): {ex.Message}");
            return null;
        }
    }

    internal static IReadOnlyList<KugouSong> ParseSongs(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Array)
            return Array.Empty<KugouSong>();

        var songs = new List<KugouSong>();
        foreach (JsonElement item in info.EnumerateArray())
        {
            string? hash = OnlineLyricsJson.GetString(item, "hash");
            string? title = OnlineLyricsJson.GetString(item, "songname");
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(title)) continue;

            songs.Add(new KugouSong(
                hash,
                title.Trim(),
                OnlineLyricsJson.GetString(item, "singername")?.Trim() ?? string.Empty,
                OnlineLyricsJson.GetString(item, "album_name")?.Trim() ?? string.Empty,
                OnlineLyricsJson.GetDouble(item, "duration")));
        }

        return songs;
    }

    // Первый кандидат поиска текстов: Kugou сам сортирует их по близости к хешу и длительности.
    internal static (string Id, string AccessKey)? ParseCandidate(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement candidate in candidates.EnumerateArray())
        {
            string? id = OnlineLyricsJson.GetString(candidate, "id");
            string? accessKey = OnlineLyricsJson.GetString(candidate, "accesskey");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(accessKey)) return (id, accessKey);
        }

        return null;
    }

    // Текст приходит в поле content как base64 от UTF-8 (иногда с BOM).
    internal static string? DecodeContent(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        string? content = OnlineLyricsJson.GetString(document.RootElement, "content");
        if (string.IsNullOrWhiteSpace(content)) return null;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(content));
        }
        catch (FormatException)
        {
            return null;
        }

        IEnumerable<string> lines = decoded.TrimStart('\uFEFF')
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !HeaderTagRegex.IsMatch(line));
        string text = string.Join('\n', lines).Trim();
        return text.Length == 0 ? null : text;
    }

    // Сначала HTTPS; если соединение не удалось (устаревшие узлы Kugou), повторяем по HTTP.
    private static async Task<string> GetStringAsync(string hostAndPath, CancellationToken token)
    {
        try
        {
            return await Http.GetStringAsync("https://" + hostAndPath, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return await Http.GetStringAsync("http://" + hostAndPath, token).ConfigureAwait(false);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        LyricsNetworkIdentity.Apply(client);
        return client;
    }
}
