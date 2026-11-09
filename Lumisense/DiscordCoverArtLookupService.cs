using System.Collections.Concurrent;

namespace Lumisense;

// Ищет обложку по артисту/названию (см. CoverArtProviders) и отдаёт HTTPS-ссылку — Discord
// показывает внешний URL напрямую. Кэшируется в памяти, чтобы не бить по API на каждый трек.
public static class DiscordCoverArtLookupService
{
    private const int CacheCapacity = 300;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);
    // Пустой результат поиска и сорванный по таймауту поиск живут в кэше недолго: сетевой сбой не должен лишать трек обложки на часы.
    private static readonly TimeSpan NotFoundCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromMinutes(2);

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
        bool lookupCompleted = false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(LookupTimeout);

            // iTunes — самый широкий охват и быстрее всех; Deezer — второй. MusicBrainz в автоматический поиск не входит:
            // это два и больше запросов на кандидата, а лимит сервиса — один запрос в секунду; он остаётся в ручном поиске обложки.
            // Берётся первый результат того же исполнителя: поиск нечёткий, и первым нередко приходит чужой трек.
            var itunesResults = await CoverArtProviders.SearchItunesAsync(query, timeoutCts.Token);
            CoverArtProviders.ArtResult? found = FirstByArtist(itunesResults, artist);

            if (found is null)
            {
                var deezerResults = await CoverArtProviders.SearchDeezerAsync(query, timeoutCts.Token);
                found = FirstByArtist(deezerResults, artist);
            }

            url = found?.FullUrl;
            lookupCompleted = true;
        }
        catch (OperationCanceledException)
        {
            // Трек сменился до завершения поиска: результата нет, и запоминать «нет обложки» нельзя — при возврате к треку поиск нужен снова.
            if (token.IsCancellationRequested) return null;
            // Иначе это таймаут лукапа — Rich Presence обновится без обложки, не критично для остальной функциональности.
        }
        catch
        {
            // Сетевые сбои не должны ломать Rich Presence.
        }

        TrimExpiredEntries();
        TimeSpan ttl = url is not null ? CacheTtl : lookupCompleted ? NotFoundCacheTtl : FailureCacheTtl;
        Cache[cacheKey] = new CacheEntry(url, DateTime.UtcNow.Add(ttl));
        return url;
    }

    private static CoverArtProviders.ArtResult? FirstByArtist(IReadOnlyList<CoverArtProviders.ArtResult> results, string requestedArtist)
    {
        foreach (var result in results)
        {
            if (ArtistMatches(requestedArtist, result.Label)) return result;
        }

        return null;
    }

    // Label у iTunes и Deezer — «исполнитель — альбом». Исполнитель считается совпавшим, если один ключ содержит другой
    // (учитывает «A & B» и «A feat. B»); если исполнитель трека неизвестен, сверять не с чем.
    internal static bool ArtistMatches(string requestedArtist, string label)
    {
        string wanted = LyricsMatcher.NormalizeKey(requestedArtist);
        if (wanted.Length == 0) return true;

        int separator = label.IndexOf(" — ", StringComparison.Ordinal);
        string found = LyricsMatcher.NormalizeKey(separator >= 0 ? label[..separator] : label);
        return found.Length > 0 && (found.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(found, StringComparison.Ordinal));
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

        // Если истёкших нет, кэш рос бы без предела: освобождаем место, убирая записи с ближайшим сроком.
        if (Cache.Count < CacheCapacity) return;
        foreach (var pair in Cache.OrderBy(p => p.Value.ExpiresAtUtc).Take(Cache.Count - CacheCapacity + 1).ToList())
            Cache.TryRemove(pair.Key, out _);
    }
}
