using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace Lumisense;

// Дополнительные источники текстов песен: NetEase Cloud Music и QQ Music через библиотеку Lyricify.Lyrics.Helper (Apache-2.0);
// Kugou и Paxsenix подключены напрямую (KugouLyricsSource, PaxsenixLyricsSource) и опрашиваются здесь же.
// Включается отдельной настройкой (AppSettings.UseExtraLyricsSource, по умолчанию выключена); запрос с названием и
// исполнителем трека уходит на серверы NetEase, QQ Music, Kugou, Paxsenix и Apple (поиск в iTunes). Библиотека сама подписывает запросы и разбирает ответы; сбой источника
// не должен мешать основному поиску по LRCLIB, поэтому любые ошибки превращаются в пустой список.
internal static class LyricifyLyricsSource
{
    public const string SourceName = "NetEase";
    public const string QqSourceName = "QQ Music";

    private const int MaxResults = 4;

    // Китайские серверы иногда отвечают очень медленно; без предела один зависший источник тормозил бы весь поиск.
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, CachedSearch> SearchCache = new();

    private sealed record CachedSearch(DateTime StoredAtUtc, IReadOnlyList<OnlineLyricsResult> Results);
    private static readonly Regex TimestampRegex = LyricsTextHygiene.Timestamp;
    private static readonly char[] ArtistSeparators = { ';', ',' };

    // NetEase и QQ Music опрашиваются параллельно: у них разные каталоги, и зарубежные треки чаще находятся хотя бы в одном.
    // Порядок результатов — сначала NetEase, затем QQ Music; сбой одного источника не мешает другому.
    private static readonly Task<IReadOnlyList<OnlineLyricsResult>> NoResults =
        Task.FromResult<IReadOnlyList<OnlineLyricsResult>>(Array.Empty<OnlineLyricsResult>());

    // onlySource — опросить только этот источник (null — все); failures собирает имена источников, которые не ответили,
    // чтобы окно поиска отличало «не ответил» от «ничего не найдено».
    public static async Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(
        string title, string artist, CancellationToken token, string? onlySource = null, ConcurrentBag<string>? failures = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<OnlineLyricsResult>();

        bool Wants(string name) => onlySource is null || string.Equals(onlySource, name, StringComparison.OrdinalIgnoreCase);

        var metadata = new TrackMultiArtistMetadata { Title = title.Trim() };
        if (!string.IsNullOrWhiteSpace(artist) && artist != "—")
        {
            metadata.Artists = artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        Task<IReadOnlyList<OnlineLyricsResult>> neteaseSearch = !Wants(SourceName) ? NoResults : SearchSourceAsync(
            SourceName, new NeteaseSearcher(), metadata,
            (result, sourceToken) => result is NeteaseSearchResult neteaseResult ? LoadNeteaseAsync(neteaseResult, sourceToken) : Task.FromResult<OnlineLyricsResult?>(null),
            failures, token);
        Task<IReadOnlyList<OnlineLyricsResult>> qqSearch = !Wants(QqSourceName) ? NoResults : SearchSourceAsync(
            QqSourceName, new QQMusicSearcher(), metadata,
            (result, sourceToken) => result is QQMusicSearchResult qqResult ? LoadQqAsync(qqResult, sourceToken) : Task.FromResult<OnlineLyricsResult?>(null),
            failures, token);

        // Kugou и Paxsenix сами обрабатывают сбои и таймауты; их результаты идут после NetEase и QQ Music.
        Task<IReadOnlyList<OnlineLyricsResult>> kugouSearch = Wants(KugouLyricsSource.SourceName)
            ? KugouLyricsSource.SearchAsync(title, artist, token) : NoResults;
        Task<IReadOnlyList<OnlineLyricsResult>> paxsenixSearch = Wants(PaxsenixLyricsSource.SourceName)
            ? PaxsenixLyricsSource.SearchAsync(title, artist, token) : NoResults;

        IReadOnlyList<OnlineLyricsResult>[] groups = await Task.WhenAll(neteaseSearch, qqSearch, kugouSearch, paxsenixSearch).ConfigureAwait(false);
        return groups.SelectMany(group => group).ToList();
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> SearchSourceAsync(
        string sourceName,
        ISearcher searcher,
        TrackMultiArtistMetadata metadata,
        Func<ISearchResult, CancellationToken, Task<OnlineLyricsResult?>> load,
        ConcurrentBag<string>? failures,
        CancellationToken token)
    {
        // Удачный ответ запоминается: китайские серверы отвечают нестабильно (лимиты на частые запросы, таймауты), и повторный
        // поиск того же трека раньше мог ничего не найти, хотя минуту назад текст нашёлся.
        string cacheKey = $"{sourceName}|{metadata.Title}|{string.Join(';', metadata.Artists ?? [])}".ToLowerInvariant();
        if (SearchCache.TryGetValue(cacheKey, out CachedSearch? cached) && DateTime.UtcNow - cached.StoredAtUtc < CacheLifetime)
            return cached.Results;

        // Две попытки: сбой или таймаут источника часто временные. Пустой ответ без ошибки — «действительно не найдено»,
        // его не повторяем.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            // Общий предел времени на попытку (поиск и загрузка текстов); отмена пользователем отличается от таймаута.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(SourceTimeout);
            CancellationToken sourceToken = timeout.Token;

            try
            {
                List<ISearchResult> found = await searcher.SearchForResults(metadata).WaitAsync(sourceToken).ConfigureAwait(false);
                if (found.Count == 0) return Array.Empty<OnlineLyricsResult>();

                // Тексты грузятся параллельно для нескольких лучших совпадений; сбой одной загрузки не отменяет остальные,
                // но считается отдельно от «текста нет» (load вернул null).
                int loadFailures = 0;
                async Task<OnlineLyricsResult?> LoadSafeAsync(ISearchResult result)
                {
                    try
                    {
                        return await load(result, sourceToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref loadFailures);
                        Logger.Info($"Текст {sourceName} не получен: {ex.Message}");
                        return null;
                    }
                }

                Task<OnlineLyricsResult?>[] tasks = found.Take(MaxResults).Select(LoadSafeAsync).ToArray();
                OnlineLyricsResult?[] loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
                List<OnlineLyricsResult> results = loaded.Where(result => result is not null).Select(result => result!).ToList();

                // Варианты нашлись, но ни один текст не удалось получить из-за сбоев (а не потому, что текстов нет): пробуем ещё раз.
                if (results.Count == 0 && loadFailures > 0) throw new InvalidOperationException("тексты найденных вариантов не загрузились");
                if (results.Count == 0) return Array.Empty<OnlineLyricsResult>();

                SearchCache[cacheKey] = new CachedSearch(DateTime.UtcNow, results);
                return results;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Источник текстов {sourceName} не ответил за {SourceTimeout.TotalSeconds:0} с (попытка {attempt})");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Источник текстов {sourceName} недоступен (попытка {attempt}): {ex.Message}");
            }

            if (attempt == 1) await Task.Delay(400, token).ConfigureAwait(false);
        }

        failures?.Add(sourceName);
        return Array.Empty<OnlineLyricsResult>();
    }

    private static async Task<OnlineLyricsResult?> LoadNeteaseAsync(NeteaseSearchResult result, CancellationToken token)
    {
        try
        {
            var lyric = await ProviderHelper.NeteaseApi.GetLyric(result.Id).WaitAsync(token).ConfigureAwait(false);
            string? text = lyric?.Lrc?.Lyric;
            if (lyric is null || lyric.Nolyric || string.IsNullOrWhiteSpace(text)) return null;

            long id = long.TryParse(result.Id, out long parsed) ? -Math.Abs(parsed) : -1;
            return CreateResult(id, result, text, SourceName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
    }

    private static async Task<OnlineLyricsResult?> LoadQqAsync(QQMusicSearchResult result, CancellationToken token)
    {
        try
        {
            var lyric = await ProviderHelper.QQMusicApi.GetLyric(result.Mid).WaitAsync(token).ConfigureAwait(false);
            string? text = lyric?.Lyric;
            if (string.IsNullOrWhiteSpace(text)) return null;

            // У QQ Music нет числового идентификатора, пригодного как ключ: берём стабильный хеш mid (string.GetHashCode
            // в .NET меняется от запуска к запуску) и уводим его в отдельный диапазон, чтобы он не пересёкся с NetEase.
            long id = -(1_000_000_000_000L + StableHash(result.Mid));
            return CreateResult(id, result, text, QqSourceName);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
    }

    // FNV-1a, 32 бита: детерминированный хеш строки.
    internal static long StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (char symbol in value)
        {
            hash ^= symbol;
            hash *= 16777619;
        }

        return hash;
    }

    // null, если в тексте одни служебные строки (авторы, композитор): такой «результат» не нужен ни в списке, ни на экране.
    private static OnlineLyricsResult? CreateResult(long id, ISearchResult result, string lyricText, string source)
    {
        string text = lyricText.Trim();
        bool synced = TimestampRegex.IsMatch(text);
        if (!LyricsTextHygiene.HasRealLyrics(synced ? text : null, synced ? null : text)) return null;

        return new OnlineLyricsResult(
            id,
            result.Title,
            // Artist — член интерфейса по умолчанию, у конкретного класса его нет: собираем строку из списка исполнителей.
            string.Join(", ", result.Artists),
            result.Album ?? string.Empty,
            (result.DurationMs ?? 0) / 1000.0,
            synced ? null : text,
            synced ? text : null,
            source);
    }
}
