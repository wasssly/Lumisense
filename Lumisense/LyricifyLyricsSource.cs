using System.Text.RegularExpressions;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;

namespace Lumisense;

// Дополнительные источники текстов песен: NetEase Cloud Music и QQ Music через библиотеку Lyricify.Lyrics.Helper (Apache-2.0).
// Включается отдельной настройкой (AppSettings.UseExtraLyricsSource, по умолчанию выключена); запрос с названием и
// исполнителем трека уходит на серверы NetEase и QQ Music. Библиотека сама подписывает запросы и разбирает ответы; сбой источника
// не должен мешать основному поиску по LRCLIB, поэтому любые ошибки превращаются в пустой список.
internal static class LyricifyLyricsSource
{
    public const string SourceName = "NetEase";
    public const string QqSourceName = "QQ Music";

    private const int MaxResults = 4;

    // Китайские серверы иногда отвечают очень медленно; без предела один зависший источник тормозил бы весь поиск.
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex TimestampRegex = new(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);
    private static readonly char[] ArtistSeparators = { ';', ',' };

    // NetEase и QQ Music опрашиваются параллельно: у них разные каталоги, и зарубежные треки чаще находятся хотя бы в одном.
    // Порядок результатов — сначала NetEase, затем QQ Music; сбой одного источника не мешает другому.
    public static async Task<IReadOnlyList<OnlineLyricsResult>> SearchAsync(string title, string artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<OnlineLyricsResult>();

        var metadata = new TrackMultiArtistMetadata { Title = title.Trim() };
        if (!string.IsNullOrWhiteSpace(artist) && artist != "—")
        {
            metadata.Artists = artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        Task<IReadOnlyList<OnlineLyricsResult>> neteaseSearch = SearchSourceAsync(
            SourceName, new NeteaseSearcher(), metadata,
            (result, sourceToken) => result is NeteaseSearchResult neteaseResult ? LoadNeteaseAsync(neteaseResult, sourceToken) : Task.FromResult<OnlineLyricsResult?>(null),
            token);
        Task<IReadOnlyList<OnlineLyricsResult>> qqSearch = SearchSourceAsync(
            QqSourceName, new QQMusicSearcher(), metadata,
            (result, sourceToken) => result is QQMusicSearchResult qqResult ? LoadQqAsync(qqResult, sourceToken) : Task.FromResult<OnlineLyricsResult?>(null),
            token);

        IReadOnlyList<OnlineLyricsResult>[] groups = await Task.WhenAll(neteaseSearch, qqSearch).ConfigureAwait(false);
        return groups.SelectMany(group => group).ToList();
    }

    private static async Task<IReadOnlyList<OnlineLyricsResult>> SearchSourceAsync(
        string sourceName,
        ISearcher searcher,
        TrackMultiArtistMetadata metadata,
        Func<ISearchResult, CancellationToken, Task<OnlineLyricsResult?>> load,
        CancellationToken token)
    {
        // Общий предел времени на источник (поиск и загрузка текстов); отмена пользователем отличается от таймаута.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SourceTimeout);
        CancellationToken sourceToken = timeout.Token;

        try
        {
            List<ISearchResult> found = await searcher.SearchForResults(metadata).WaitAsync(sourceToken).ConfigureAwait(false);
            if (found.Count == 0) return Array.Empty<OnlineLyricsResult>();

            // Тексты запрашиваются параллельно только для нескольких лучших совпадений.
            Task<OnlineLyricsResult?>[] tasks = found.Take(MaxResults).Select(result => load(result, sourceToken)).ToArray();
            OnlineLyricsResult?[] loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
            return loaded.Where(result => result is not null).Select(result => result!).ToList();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Logger.Info($"Источник текстов {sourceName} не ответил за {SourceTimeout.TotalSeconds:0} с");
            return Array.Empty<OnlineLyricsResult>();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Источник текстов {sourceName} недоступен: {ex.Message}");
            return Array.Empty<OnlineLyricsResult>();
        }
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
        catch (Exception ex)
        {
            Logger.Info($"Текст NetEase не получен ({result.Id}): {ex.Message}");
            return null;
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
        catch (Exception ex)
        {
            Logger.Info($"Текст QQ Music не получен ({result.Mid}): {ex.Message}");
            return null;
        }
    }

    // FNV-1a, 32 бита: детерминированный хеш строки.
    private static long StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (char symbol in value)
        {
            hash ^= symbol;
            hash *= 16777619;
        }

        return hash;
    }

    private static OnlineLyricsResult CreateResult(long id, ISearchResult result, string lyricText, string source)
    {
        string text = lyricText.Trim();
        bool synced = TimestampRegex.IsMatch(text);
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
