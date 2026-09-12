using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumisense;

// Общий поиск обложки для CoverArtSearchWindow и DiscordCoverArtLookupService. У MusicBrainz
// нет своих обложек — SearchMusicBrainzAsync делает второй запрос на кандидата в Cover Art
// Archive, поэтому медленнее iTunes/Deezer, но находит то, чего нет у них.
public static class CoverArtProviders
{
    public const int MaxApiJsonBytes = 2 * 1024 * 1024;

    // MusicBrainz требует описательный User-Agent с контактом — без него сервис вправе замедлять
    // или блокировать запросы. См. https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting.
    private const string MusicBrainzUserAgent = "Lumisense/1.0 (+https://github.com/wasssly/Lumisense)";

    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static readonly HashSet<string> TrustedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mzstatic.com", "apple.com", "deezer.com", "dzcdn.net", "coverartarchive.org", "archive.org"
    };

    // Единая карточка результата независимо от источника. Source — только для UI (подпись/фильтр
    // в галерее результатов), на логику поиска не влияет.
    public readonly record struct ArtResult(string ThumbUrl, string FullUrl, string Label, string Source);

    // ---------- iTunes Search API ----------

    public static async Task<List<ArtResult>> SearchItunesAsync(string query, CancellationToken token)
    {
        try
        {
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&entity=song&limit=16";
            using var response = await Http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            var json = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes, token));
            return ParseItunesResults(json);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Сеть недоступна, iTunes вернул ошибку, JSON не распарсился, истёк таймаут и т.п. —
            // во всех этих случаях остальные источники всё ещё могут найти результат, поэтому
            // просто отдаём пустой список вместо того, чтобы обрушить весь поиск целиком.
            return new List<ArtResult>();
        }
    }

    // Разбирает ответ iTunes Search API и схлопывает повторы одной и той же обложки у
    // разных треков одного альбома (artworkUrl уникален на альбом, а не на трек).
    private static List<ArtResult> ParseItunesResults(string json)
    {
        var entries = new List<ArtResult>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return entries;

        var seenArt = new HashSet<string>();
        foreach (var item in results.EnumerateArray())
        {
            var artwork = item.TryGetProperty("artworkUrl100", out var artEl) ? artEl.GetString() : null;
            if (string.IsNullOrEmpty(artwork) || !seenArt.Add(artwork)) continue;

            var trackArtist = item.TryGetProperty("artistName", out var aEl) ? aEl.GetString() : "";
            var collection = item.TryGetProperty("collectionName", out var cEl) ? cEl.GetString() : "";
            var label = string.IsNullOrEmpty(collection) ? trackArtist ?? "" : $"{trackArtist} — {collection}";

            entries.Add(new ArtResult(WithItunesArtworkSize(artwork, 200), WithItunesArtworkSize(artwork, 1200), label, "iTunes"));
        }

        return entries;
    }

    // Ссылки iTunes на обложки содержат размер прямо в пути (например ".../100x100bb.jpg") —
    // подставляя своё значение вместо 100, можно получить то же изображение в нужном разрешении.
    private static string WithItunesArtworkSize(string artworkUrl, int size) =>
        Regex.Replace(artworkUrl, @"\d+x\d+bb(?=\.\w+$)", $"{size}x{size}bb");

    // ---------- Deezer Search API ----------

    public static async Task<List<ArtResult>> SearchDeezerAsync(string query, CancellationToken token)
    {
        try
        {
            var url = $"https://api.deezer.com/search?q={Uri.EscapeDataString(query)}&limit=16";
            using var response = await Http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            var json = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes, token));
            return ParseDeezerResults(json);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // См. подробный комментарий в SearchItunesAsync — сюда же попадает и таймаут
            // самого HttpClient, а не только настоящая отмена.
            return new List<ArtResult>();
        }
    }

    // Разбирает ответ Deezer Search API и схлопывает повторы одной и той же обложки у разных
    // треков одного альбома, как и для iTunes выше.
    private static List<ArtResult> ParseDeezerResults(string json)
    {
        var entries = new List<ArtResult>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return entries;

        var seenArt = new HashSet<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("album", out var album)) continue;

            var thumb = album.TryGetProperty("cover_medium", out var thumbEl) ? thumbEl.GetString() : null;
            thumb ??= album.TryGetProperty("cover_big", out var thumbBigEl) ? thumbBigEl.GetString() : null;
            if (string.IsNullOrEmpty(thumb) || !seenArt.Add(thumb)) continue;

            var full = album.TryGetProperty("cover_xl", out var fullEl) ? fullEl.GetString() : null;
            full ??= album.TryGetProperty("cover_big", out var fullBigEl) ? fullBigEl.GetString() : null;
            full ??= thumb;

            var trackArtist = item.TryGetProperty("artist", out var artistEl) && artistEl.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() : "";
            var albumTitle = album.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : "";
            var label = string.IsNullOrEmpty(albumTitle) ? trackArtist ?? "" : $"{trackArtist} — {albumTitle}";

            entries.Add(new ArtResult(thumb, full, label, "Deezer"));
        }

        return entries;
    }

    // ---------- MusicBrainz (сопоставление) + Cover Art Archive (сама картинка) ----------

    public static async Task<List<ArtResult>> SearchMusicBrainzAsync(string query, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length == 0) return new List<ArtResult>();

        try
        {
            var url = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=8";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(MusicBrainzUserAgent);
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var json = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes, token));

            var candidates = ParseMusicBrainzReleaseCandidates(json);
            if (candidates.Count == 0) return new List<ArtResult>();

            // На каждый найденный релиз — отдельный запрос к Cover Art Archive: у MusicBrainz
            // самой обложки нет. Ограничиваем число кандидатов, чтобы не устраивать десятки
            // последовательных запросов на один поиск — Cover Art Archive не резиновый.
            var results = new List<ArtResult>();
            var seenReleaseIds = new HashSet<string>();
            foreach (var (releaseId, label) in candidates)
            {
                if (results.Count >= 6) break;
                if (!seenReleaseIds.Add(releaseId)) continue;

                token.ThrowIfCancellationRequested();
                if (await TryGetCoverArtArchiveFrontAsync(releaseId, label, token) is { } found)
                    results.Add(found);
            }
            return results;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new List<ArtResult>();
        }
    }

    // Разбирает ответ MusicBrainz recording-поиска: у каждой найденной записи (recording) сразу
    // приходит список связанных релизов (альбом/сингл/переиздание), без отдельного запроса.
    // Берём не больше двух релизов на запись — иначе один популярный трек с полусотней
    // переизданий полностью забьёт лимит кандидатов для Cover Art Archive.
    private static List<(string ReleaseId, string Label)> ParseMusicBrainzReleaseCandidates(string json)
    {
        var candidates = new List<(string, string)>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            return candidates;

        foreach (var recording in recordings.EnumerateArray())
        {
            string artistName = "";
            if (recording.TryGetProperty("artist-credit", out var artistCredit) && artistCredit.ValueKind == JsonValueKind.Array)
            {
                artistName = string.Join(" ", artistCredit.EnumerateArray()
                    .Select(ac => ac.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null)
                    .Where(name => !string.IsNullOrEmpty(name)));
            }

            if (!recording.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
                continue;

            int takenForThisRecording = 0;
            foreach (var release in releases.EnumerateArray())
            {
                if (takenForThisRecording >= 2) break;

                var releaseId = release.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(releaseId)) continue;

                var releaseTitle = release.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var label = string.IsNullOrEmpty(releaseTitle) ? artistName : $"{artistName} — {releaseTitle}";

                candidates.Add((releaseId, label));
                takenForThisRecording++;
            }
        }

        return candidates;
    }

    // Cover Art Archive отдаёт 404 для релизов без обложки в архиве — это нормальный, ожидаемый
    // исход (не у каждого релиза MusicBrainz есть скан обложки), а не ошибка сети.
    private static async Task<ArtResult?> TryGetCoverArtArchiveFrontAsync(string releaseId, string label, CancellationToken token)
    {
        try
        {
            var url = $"https://coverartarchive.org/release/{Uri.EscapeDataString(releaseId)}";
            using var response = await Http.GetAsync(url, token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            var json = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes, token));

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var image in images.EnumerateArray())
            {
                bool isFront = image.TryGetProperty("front", out var frontEl) && frontEl.ValueKind == JsonValueKind.True;
                if (!isFront) continue;

                string? full = image.TryGetProperty("image", out var imageEl) ? imageEl.GetString() : null;
                string? thumb = null;
                if (image.TryGetProperty("thumbnails", out var thumbs))
                {
                    thumb = thumbs.TryGetProperty("500", out var t500) ? t500.GetString()
                        : thumbs.TryGetProperty("250", out var t250) ? t250.GetString()
                        : thumbs.TryGetProperty("large", out var tLarge) ? tLarge.GetString()
                        : null;
                }
                thumb ??= full;
                full ??= thumb;
                if (string.IsNullOrEmpty(thumb) || string.IsNullOrEmpty(full)) continue;

                return new ArtResult(thumb, full, label, "MusicBrainz");
            }

            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Общее ----------

    public static async Task<byte[]> ReadBytesWithLimitAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw new IOException("Ответ превышает допустимый размер.");

        await using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > maxBytes)
                throw new IOException("Ответ превышает допустимый размер.");
            await memory.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return memory.ToArray();
    }
}
