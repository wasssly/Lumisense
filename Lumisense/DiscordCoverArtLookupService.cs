using System.Collections.Concurrent;

namespace Lumisense;

// Ищет обложку по артисту/названию (см. CoverArtProviders) и отдаёт HTTPS-ссылку — Discord
// показывает внешний URL напрямую. Кэшируется в памяти, чтобы не бить по API на каждый трек.
public static class DiscordCoverArtLookupService
{
    private const int CacheCapacity = 300;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);

    private sealed record CacheEntry(string? Url, DateTime ExpiresAtUtc);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    // null означает и "точно нет обложки", и "не удалось найти за отведённое время" — в обоих
    // случаях Rich Presence просто не показывает Assets, а не блокируется в ожидании ответа.
    public static async Task<string?> TryGetCoverUrlAsync(string artist, string title, CancellationToken token)
    {
        var query = string.Join(" ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (query.Length == 0) return null;

        var cacheKey = query.Trim().ToLowerInvariant();
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            return cached.Url;

        string? url = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(LookupTimeout);

            // iTunes — самый широкий охват и быстрее всех; Deezer — второй; MusicBrainz —
            // минимум два запроса на кандидата, поэтому только если первые два ничего не нашли.
            var itunesResults = await CoverArtProviders.SearchItunesAsync(query, timeoutCts.Token);
            var found = itunesResults.Count > 0 ? itunesResults[0] : (CoverArtProviders.ArtResult?)null;

            if (found is null)
            {
                var deezerResults = await CoverArtProviders.SearchDeezerAsync(query, timeoutCts.Token);
                if (deezerResults.Count > 0) found = deezerResults[0];
            }

            if (found is null)
            {
                var musicBrainzResults = await CoverArtProviders.SearchMusicBrainzAsync(query, timeoutCts.Token);
                if (musicBrainzResults.Count > 0) found = musicBrainzResults[0];
            }

            url = found?.FullUrl;
        }
        catch (OperationCanceledException)
        {
            // Таймаут лукапа или отмена (например, трек сменился до завершения поиска) — Rich
            // Presence обновится без обложки, не критично для остальной функциональности.
        }
        catch
        {
            // Сетевые сбои не должны ломать Rich Presence.
        }

        TrimExpiredEntries();
        Cache[cacheKey] = new CacheEntry(url, DateTime.UtcNow.Add(CacheTtl));
        return url;
    }

    private static void TrimExpiredEntries()
    {
        if (Cache.Count < CacheCapacity) return;

        var now = DateTime.UtcNow;
        foreach (var pair in Cache)
        {
            if (pair.Value.ExpiresAtUtc <= now)
                Cache.TryRemove(pair.Key, out _);
        }
    }
}
