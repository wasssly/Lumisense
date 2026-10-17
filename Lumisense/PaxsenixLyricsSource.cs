using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumisense;

// Paxsenix (lyrics.paxsenix.org): открытый прокси к текстам Apple Music и YouTube. Для Apple Music песню сначала ищем в
// публичном каталоге iTunes (нужен числовой id), затем берём текст у Paxsenix; для YouTube — поиск видео и LRC по его id.
// Работает только при включённой настройке дополнительных источников (AppSettings.UseExtraLyricsSource): название и
// исполнитель трека уходят на серверы Paxsenix и Apple. Любой сбой источника превращается в пустой список.
internal static class PaxsenixLyricsSource
{
    public const string SourceName = "Paxsenix";

    private const string Root = "https://lyrics.paxsenix.org";
    private const string ItunesSearch = "https://itunes.apple.com/search";
    private const int MaxAppleSongs = 3;
    private const int MaxYoutubeVideos = 2;
    // Apple Music у Paxsenix отвечает медленно: ему отведено больше времени, чем остальным источникам.
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex TimestampRegex = new(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateHttpClient();

    internal sealed record ItunesSong(long TrackId, string Title, string Artist, string Album, double DurationSeconds);

    internal sealed record YoutubeVideo(string Id, string Title, string Author, double DurationSeconds);

    public static async Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<OnlineLyricsResult>();

        string query = (string.IsNullOrWhiteSpace(artist) || artist == "—" ? title : $"{title} {artist}").Trim();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SourceTimeout);
        CancellationToken sourceToken = timeout.Token;

        // Каждая ветка защищена отдельно: таймаут или сбой одной не должны отбрасывать результат другой.
        IReadOnlyList<OnlineLyricsResult>[] groups = await Task.WhenAll(
            GuardAsync("Apple Music", () => SearchAppleMusicAsync(query, sourceToken), token),
            GuardAsync("YouTube", () => SearchYoutubeAsync(query, sourceToken), token)).ConfigureAwait(false);
        return groups.SelectMany(group => group).ToList();
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> GuardAsync(
        string branch,
        Func<Task<IReadOnlyList<OnlineLyricsResult>>> work,
        CancellationToken userToken)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Logger.Info($"Источник текстов {SourceName} ({branch}) не ответил за {SourceTimeout.TotalSeconds:0} с");
            return Array.Empty<OnlineLyricsResult>();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Источник текстов {SourceName} ({branch}) недоступен: {ex.Message}");
            return Array.Empty<OnlineLyricsResult>();
        }
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> SearchAppleMusicAsync(string query, CancellationToken token)
    {
        string json = await Http.GetStringAsync(
            $"{ItunesSearch}?term={Uri.EscapeDataString(query)}&entity=song&media=music&limit=8", token).ConfigureAwait(false);
        IReadOnlyList<ItunesSong> songs = ParseItunesSongs(json);
        if (songs.Count == 0) return Array.Empty<OnlineLyricsResult>();

        Task<OnlineLyricsResult?>[] tasks = songs.Take(MaxAppleSongs).Select(song => LoadAppleLyricsAsync(song, token)).ToArray();
        OnlineLyricsResult?[] loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
        return loaded.Where(result => result is not null).Select(result => result!).ToList();
    }

    private static async Task<OnlineLyricsResult?> LoadAppleLyricsAsync(ItunesSong song, CancellationToken token)
    {
        try
        {
            string json = await Http.GetStringAsync($"{Root}/apple-music/lyrics?id={song.TrackId}", token).ConfigureAwait(false);
            (string? synced, string? plain) = ParseAppleLyrics(json);
            if (synced is null && plain is null) return null;

            return new OnlineLyricsResult(-(3_000_000_000_000L + song.TrackId), song.Title, song.Artist, song.Album,
                song.DurationSeconds, plain, synced, SourceName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Info($"Текст Paxsenix (Apple Music) не получен ({song.TrackId}): {ex.Message}");
            return null;
        }
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> SearchYoutubeAsync(string query, CancellationToken token)
    {
        string json = await Http.GetStringAsync($"{Root}/youtube/search?q={Uri.EscapeDataString(query)}", token).ConfigureAwait(false);
        IReadOnlyList<YoutubeVideo> videos = ParseYoutubeVideos(json);
        if (videos.Count == 0) return Array.Empty<OnlineLyricsResult>();

        Task<OnlineLyricsResult?>[] tasks = videos.Take(MaxYoutubeVideos).Select(video => LoadYoutubeLyricsAsync(video, token)).ToArray();
        OnlineLyricsResult?[] loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
        return loaded.Where(result => result is not null).Select(result => result!).ToList();
    }

    private static async Task<OnlineLyricsResult?> LoadYoutubeLyricsAsync(YoutubeVideo video, CancellationToken token)
    {
        try
        {
            string lrc = (await Http.GetStringAsync($"{Root}/youtube/lyrics?id={Uri.EscapeDataString(video.Id)}", token).ConfigureAwait(false)).Trim();
            if (!HasUsableTiming(lrc)) return null;

            long id = -(4_000_000_000_000L + LyricifyLyricsSource.StableHash(video.Id));
            return new OnlineLyricsResult(id, video.Title, video.Author, string.Empty, video.DurationSeconds, null, lrc, SourceName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Info($"Текст Paxsenix (YouTube) не получен ({video.Id}): {ex.Message}");
            return null;
        }
    }

    internal static IReadOnlyList<ItunesSong> ParseItunesSongs(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
            return Array.Empty<ItunesSong>();

        var songs = new List<ItunesSong>();
        foreach (JsonElement item in results.EnumerateArray())
        {
            if (!long.TryParse(OnlineLyricsJson.GetString(item, "trackId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long trackId)) continue;
            string? title = OnlineLyricsJson.GetString(item, "trackName");
            if (string.IsNullOrWhiteSpace(title)) continue;

            songs.Add(new ItunesSong(
                trackId,
                title.Trim(),
                OnlineLyricsJson.GetString(item, "artistName")?.Trim() ?? string.Empty,
                OnlineLyricsJson.GetString(item, "collectionName")?.Trim() ?? string.Empty,
                OnlineLyricsJson.GetDouble(item, "trackTimeMillis") / 1000.0));
        }

        return songs;
    }

    // Ответ Paxsenix для Apple Music: готовый lrc, построчный content (с разбивкой на слоги) или обычный plain.
    internal static (string? Synced, string? Plain) ParseAppleLyrics(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string? lrc = OnlineLyricsJson.GetString(root, "lrc");
        if (HasUsableTiming(lrc)) return (lrc!.Trim(), null);

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            string? built = BuildLrc(content);
            if (built is not null) return (built, null);
        }

        string? plain = OnlineLyricsJson.GetString(root, "plain");
        return (null, string.IsNullOrWhiteSpace(plain) ? null : plain.Trim());
    }

    internal static IReadOnlyList<YoutubeVideo> ParseYoutubeVideos(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement items = document.RootElement;
        // Бывает и голый массив, и объект с массивом внутри.
        if (items.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in new[] { "results", "data", "items" })
            {
                if (items.TryGetProperty(name, out JsonElement inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    items = inner;
                    break;
                }
            }
        }

        if (items.ValueKind != JsonValueKind.Array) return Array.Empty<YoutubeVideo>();

        var videos = new List<YoutubeVideo>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            string? id = OnlineLyricsJson.GetFirstString(item, "videoId", "video_id", "id");
            string? title = OnlineLyricsJson.GetString(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;

            videos.Add(new YoutubeVideo(
                id,
                title.Trim(),
                OnlineLyricsJson.GetFirstString(item, "author", "artist", "channel")?.Trim() ?? string.Empty,
                ParseColonDuration(OnlineLyricsJson.GetString(item, "duration"))));
        }

        return videos;
    }

    // «3:25» или «1:02:03» → секунды; неразобранное значение — 0 (длительность неизвестна).
    internal static double ParseColonDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        double total = 0;
        foreach (string part in value.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return 0;
            total = total * 60 + number;
        }

        return total;
    }

    // Собирает LRC из построчных данных Apple Music; фоновые партии (бэк-вокал) пропускаются, чтобы не удваивать строки.
    internal static string? BuildLrc(JsonElement content)
    {
        var builder = new StringBuilder();
        var starts = new HashSet<long>();
        foreach (JsonElement row in content.EnumerateArray())
        {
            if (OnlineLyricsJson.GetBool(row, "background")) continue;

            string text = RowText(row);
            if (text.Length == 0) continue;

            long start = (long)OnlineLyricsJson.GetDouble(row, "timestamp");
            starts.Add(start);
            TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, start));
            builder.Append('[')
                .Append(((int)time.TotalMinutes).ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append(time.Seconds.ToString("00", CultureInfo.InvariantCulture)).Append('.')
                .Append((time.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture))
                .Append(']').Append(text).Append('\n');
        }

        // Без различимых меток времени (все нули) такой LRC бесполезен.
        return starts.Count >= 2 ? builder.ToString().Trim() : null;
    }

    private static string RowText(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("text", out JsonElement text)) return string.Empty;
        if (text.ValueKind == JsonValueKind.String) return text.GetString()?.Trim() ?? string.Empty;
        if (text.ValueKind != JsonValueKind.Array) return string.Empty;

        // Слоги склеиваются без пробела, если предыдущий помечен part=true; перед знаками препинания пробела нет.
        var builder = new StringBuilder();
        bool previousContinues = false;
        foreach (JsonElement part in text.EnumerateArray())
        {
            string? piece = OnlineLyricsJson.GetString(part, "text");
            if (string.IsNullOrWhiteSpace(piece)) continue;

            bool noSpace = builder.Length == 0 || previousContinues || ",.?!:;)]}'’".Contains(piece[0]);
            if (!noSpace) builder.Append(' ');
            builder.Append(piece);
            previousContinues = OnlineLyricsJson.GetBool(part, "part");
        }

        return builder.ToString().Trim();
    }

    internal static bool HasUsableTiming(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return false;
        return TimestampRegex.Matches(lrc).Select(match => match.Value).Distinct().Count() >= 2;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        LyricsNetworkIdentity.Apply(client);
        return client;
    }
}
