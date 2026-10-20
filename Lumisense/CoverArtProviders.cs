using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumisense;

// Общий поиск обложки для CoverArtSearchWindow и DiscordCoverArtLookupService. MusicBrainz
// сам обложек не хранит — SearchMusicBrainzAsync делает второй запрос в Cover Art Archive.
public static class CoverArtProviders
{
    public const int MaxApiJsonBytes = 2 * 1024 * 1024;

    // MusicBrainz требует описательный User-Agent с контактом — без него сервис вправе замедлять
    // или блокировать запросы. См. https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting.
    private const string MusicBrainzUserAgent = "Lumisense/1.0 (+https://github.com/wasssly/Lumisense)";

    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static readonly HashSet<string> TrustedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mzstatic.com", "apple.com", "deezer.com", "dzcdn.net", "coverartarchive.org", "archive.org", "yandex.net",
        "genius.com", "rapgenius.com", "sndcdn.com"
    };

    // Единая карточка результата независимо от источника. Source — только для UI (подпись/фильтр
    // в галерее результатов), на логику поиска не влияет.
    public readonly record struct ArtResult(string ThumbUrl, string FullUrl, string Label, string Source);

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
            // Любой сбой источника (сеть, JSON, таймаут) не роняет поиск: остальные источники ещё могут найти обложку.
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

    // Яндекс Музыка: неофициальный публичный поиск api.music.yandex.net без авторизации (токен не используется).
    // Это собственный небольшой клиент; сторонняя библиотека-обёртка (GPL-3.0) намеренно не подключена. Если сервис
    // изменит или закроет доступ, источник просто вернёт пустой список, как и остальные.
    public static async Task<List<ArtResult>> SearchYandexMusicAsync(string query, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length == 0) return new List<ArtResult>();

        try
        {
            var url = $"https://api.music.yandex.net/search?text={Uri.EscapeDataString(query)}&type=track&page=0&pageSize=16";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", MusicBrainzUserAgent);
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var json = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes, token));
            return ParseYandexMusicResults(json);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // См. комментарий в SearchItunesAsync: сбой одного источника не должен ломать остальные.
            return new List<ArtResult>();
        }
    }

    // Ответ вида {"result":{"tracks":{"results":[{"title","artists":[{"name"}],"albums":[{"title","coverUri"}],"coverUri"}]}}}.
    // coverUri приходит без схемы и с маркером размера "%%", например "avatars.yandex.net/get-music-content/.../%%".
    private static List<ArtResult> ParseYandexMusicResults(string json)
    {
        var entries = new List<ArtResult>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("tracks", out var tracks)
            || !tracks.TryGetProperty("results", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return entries;

        var seenArt = new HashSet<string>();
        foreach (var item in items.EnumerateArray())
        {
            var coverUri = item.TryGetProperty("coverUri", out var coverEl) ? coverEl.GetString() : null;
            string? albumTitle = null;
            if (item.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array && albums.GetArrayLength() > 0)
            {
                var album = albums[0];
                albumTitle = album.TryGetProperty("title", out var albumTitleEl) ? albumTitleEl.GetString() : null;
                if (string.IsNullOrEmpty(coverUri))
                    coverUri = album.TryGetProperty("coverUri", out var albumCoverEl) ? albumCoverEl.GetString() : null;
            }
            if (string.IsNullOrEmpty(coverUri) || !coverUri.Contains("%%") || !seenArt.Add(coverUri)) continue;

            var artists = new List<string>();
            if (item.TryGetProperty("artists", out var artistsEl) && artistsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var artist in artistsEl.EnumerateArray())
                {
                    var name = artist.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (!string.IsNullOrEmpty(name)) artists.Add(name);
                }
            }

            var artistText = string.Join(", ", artists);
            var label = string.IsNullOrEmpty(albumTitle) ? artistText : $"{artistText} — {albumTitle}";
            entries.Add(new ArtResult("https://" + coverUri.Replace("%%", "200x200"), "https://" + coverUri.Replace("%%", "1000x1000"), label, "Yandex Music"));
        }

        return entries;
    }

    // Genius: у треков, которых нет в каталогах, обложка часто есть только здесь. Поиск идёт по неофициальному
    // веб-эндпоинту сайта (ключа нет), поэтому при смене формата ответа результат будет пустым. Ссылка на картинку
    // images.genius.com или на страницу песни тоже принимается.
    public static async Task<List<ArtResult>> SearchGeniusAsync(string query, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length == 0) return new List<ArtResult>();

        try
        {
            if (IsGeniusLink(query))
            {
                if (new Uri(query).Host.StartsWith("images.", StringComparison.OrdinalIgnoreCase))
                    return new List<ArtResult> { new(query, query, "", "Genius") };

                return await ReadGeniusPageAsync(query, token);
            }

            var url = $"https://genius.com/api/search/song?q={Uri.EscapeDataString(query)}&per_page=10";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(MusicBrainzUserAgent);
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var json = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes, token));
            return ParseGeniusSearchResults(json);
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

    private static async Task<List<ArtResult>> ReadGeniusPageAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(MusicBrainzUserAgent);
        using var response = await Http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var html = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes * 2, token));
        return ParseGeniusPage(html);
    }

    // SoundCloud не даёт поиска по названию без ключа клиента, поэтому работает только вставленная ссылка на трек.
    public static async Task<List<ArtResult>> SearchSoundCloudAsync(string query, CancellationToken token)
    {
        query = query.Trim();
        if (!IsHostedLink(query, "soundcloud.com")) return new List<ArtResult>();

        try
        {
            var url = $"https://soundcloud.com/oembed?format=json&url={Uri.EscapeDataString(query)}";
            using var response = await Http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            var json = Encoding.UTF8.GetString(await ReadBytesWithLimitAsync(response.Content, MaxApiJsonBytes, token));
            return ParseSoundCloudOEmbed(json);
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

    internal static bool IsHostedLink(string text, string domain)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
               (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsGeniusLink(string text) => IsHostedLink(text, "genius.com") || IsHostedLink(text, "rapgenius.com");

    // Ответ сайта: response.sections[].hits[].result; у официального API — response.hits[].result. Понимаем оба вида.
    internal static List<ArtResult> ParseGeniusSearchResults(string json)
    {
        var results = new List<ArtResult>();
        var seen = new HashSet<string>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response)) return results;

        var hits = new List<JsonElement>();
        if (response.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in sections.EnumerateArray())
                if (section.TryGetProperty("hits", out var sectionHits) && sectionHits.ValueKind == JsonValueKind.Array)
                    hits.AddRange(sectionHits.EnumerateArray());
        }
        else if (response.TryGetProperty("hits", out var directHits) && directHits.ValueKind == JsonValueKind.Array)
        {
            hits.AddRange(directHits.EnumerateArray());
        }

        foreach (var hit in hits)
        {
            if (!hit.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) continue;

            var full = ReadString(result, "song_art_image_url");
            var thumb = ReadString(result, "song_art_image_thumbnail_url");
            if (string.IsNullOrEmpty(full)) full = thumb;
            if (string.IsNullOrEmpty(full) || !IsGeniusLink(full)) continue;
            if (string.IsNullOrEmpty(thumb) || !IsGeniusLink(thumb)) thumb = full;
            if (full.Contains("default_cover_image", StringComparison.OrdinalIgnoreCase) || !seen.Add(full)) continue;

            var artist = ReadString(result, "artist_names");
            var title = ReadString(result, "title");
            var label = string.IsNullOrEmpty(artist) ? title ?? "" : $"{artist} — {title}";
            results.Add(new ArtResult(thumb, full, label, "Genius"));
            if (results.Count >= 8) break;
        }
        return results;
    }

    internal static List<ArtResult> ParseGeniusPage(string html)
    {
        var image = Regex.Match(html, "<meta[^>]+property=\"og:image\"[^>]+content=\"([^\"]+)\"");
        if (!image.Success) return new List<ArtResult>();

        var url = WebUtility.HtmlDecode(image.Groups[1].Value);
        if (!IsGeniusLink(url)) return new List<ArtResult>();

        var title = Regex.Match(html, "<meta[^>]+property=\"og:title\"[^>]+content=\"([^\"]*)\"");
        var label = title.Success ? CleanHtmlText(title.Groups[1].Value) : "";
        return new List<ArtResult> { new(url, url, label, "Genius") };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static List<ArtResult> ParseSoundCloudOEmbed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var thumb = root.TryGetProperty("thumbnail_url", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrEmpty(thumb) || !IsHostedLink(thumb, "sndcdn.com")) return new List<ArtResult>();

        // oEmbed отдаёт 100 px (-large); -t500x500 есть у всех обложек SoundCloud.
        var full = Regex.Replace(thumb, @"-(large|t\d+x\d+)(\.\w+)$", "-t500x500$2");
        var label = root.TryGetProperty("title", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
        return new List<ArtResult> { new(thumb, full, label, "SoundCloud") };
    }

    private static string CleanHtmlText(string html)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

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

            // Обложку MusicBrainz отдаёт Cover Art Archive отдельным запросом на релиз, поэтому число кандидатов ограничено.
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

    // Релизы приходят вместе с записью; берём не больше двух на запись, иначе популярный трек с полусотней
    // переизданий забьёт лимит кандидатов для Cover Art Archive.
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
