using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lumisense;

public enum LyricsKind
{
    None,
    Plain,
    Synced
}

public sealed record LyricLine(TimeSpan Time, string Text);

// Сводка локального кэша вручную вставленных текстов. В кэше хранятся только тексты и
// непрозрачные SHA-256-имена файлов, а исходный путь к аудио в файл не записывается.
public readonly record struct LyricsCacheInfo(int EntryCount, long TotalBytes)
{
    public bool IsEmpty => EntryCount == 0;
}

public sealed record LyricsDocument(LyricsKind Kind, IReadOnlyList<LyricLine> Lines, string PlainText, string SourceLabel)
{
    public static readonly LyricsDocument Empty = new(LyricsKind.None, Array.Empty<LyricLine>(), string.Empty, "Нет текста");
}

// Результат встроенного поиска LRCLIB. Сохраняем только необходимые для выбора и отображения
// поля: текст не выводится в списке результатов, но остаётся в памяти до явного выбора пользователя.
public sealed record OnlineLyricsResult(
    long Id,
    string TrackName,
    string ArtistName,
    string AlbumName,
    double Duration,
    string? PlainLyrics,
    string? SyncedLyrics,
    string Source = "LRCLIB")
{
    public bool HasSyncedLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool HasLyrics => HasSyncedLyrics || !string.IsNullOrWhiteSpace(PlainLyrics);
    private string BaseName => string.IsNullOrWhiteSpace(ArtistName)
        ? TrackName
        : $"{ArtistName} — {TrackName}";

    // Результаты дополнительного источника помечены его названием, чтобы их можно было отличить от LRCLIB.
    public string DisplayName => Source == "LRCLIB" ? BaseName : $"{BaseName} \u00b7 {Source}";
}

// Локальная загрузка текстов и поиск через LRCLIB. Now Playing может сделать один авто-запрос
// после отсутствующего локального текста, но сохраняет только при точном совпадении названия/исполнителя.
public static class LyricsService
{
    private const string LrcLibSearchEndpoint = "https://lrclib.net/api/search";
    private const int MaxSearchResponseBytes = 1_500_000;
    private const int MaxCachedLyricsBytes = 2 * 1024 * 1024;
    private const int MaxCachedLyricsEntries = 200;
    private const long MaxTotalCachedLyricsBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan CachedLyricsRetention = TimeSpan.FromDays(180);
    private static readonly object CacheMaintenanceGate = new();
    // Все тексты, которые создаёт Lumisense, живут отдельно от музыкальной библиотеки.
    // Подпапки сохраняют тип содержимого понятным: lrc — синхронный текст, txt — обычный.
    private static readonly string ManagedLyricsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumisense", "lyrics");
    private static readonly string PastedLyricsCacheDirectory = Path.Combine(ManagedLyricsDirectory, "txt");
    // Путь прежних вставленных текстов до введения общей папки lyrics. Он читается только
    // как fallback, чтобы обновление не скрывало уже сохранённые данные пользователя.
    private static readonly string LegacyPastedLyricsCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumisense", "lyrics-cache");
    // Синхронные тексты, полученные Lumisense, не смешиваются с музыкальной библиотекой.
    // Имя — SHA-256 от канонического пути аудиофайла, поэтому путь пользователя в кэше не хранится.
    private static readonly string ManagedLrcDirectory = Path.Combine(ManagedLyricsDirectory, "lrc");
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim SearchGate = new(1, 1);
    private const int MaxLrcLibRateLimitAttempts = 3;
    private const int MaxLrcLibTransientAttempts = 2;

    private static readonly Regex TimestampRegex = new(
        @"\[(?<minutes>\d{1,3}):(?<seconds>\d{2})(?:[\.:](?<fraction>\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OffsetRegex = new(
        @"^\[offset:(?<milliseconds>-?\d+)\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<LyricsDocument> LoadAsync(string? audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return LyricsDocument.Empty;

        // После проверки выше сохраняем non-null путь в отдельную локальную переменную: она
        // безопасно захватывается фоновой задачей чтения тегов без nullable-предупреждения.
        string confirmedAudioPath = audioPath;

        // Сначала LRC из собственной папки Lumisense (онлайн-поиск не создаёт .lrc рядом с аудио), затем
        // LRC, положенный пользователем рядом с треком вручную.
        LyricsDocument managedLrc = await LoadSyncedLrcAsync(GetManagedLrcPath(confirmedAudioPath), cancellationToken)
            .ConfigureAwait(false);
        if (managedLrc.Kind == LyricsKind.Synced) return managedLrc;

        LyricsDocument sidecarLrc = await LoadSyncedLrcAsync(
            Path.ChangeExtension(confirmedAudioPath, ".lrc"), cancellationToken).ConfigureAwait(false);
        if (sidecarLrc.Kind == LyricsKind.Synced) return sidecarLrc;

        // Все обычные тексты, добавленные или найденные Lumisense, хранятся в txt-папке
        // приложения. TXT рядом с треком остаётся только пользовательским fallback.
        LyricsDocument managedText = await LoadPastedLyricsCacheAsync(confirmedAudioPath, cancellationToken).ConfigureAwait(false);
        if (managedText.Kind != LyricsKind.None) return managedText;

        LyricsDocument sidecarText = await LoadPlainTextAsync(
            Path.ChangeExtension(confirmedAudioPath, ".txt"), cancellationToken).ConfigureAwait(false);
        if (sidecarText.Kind == LyricsKind.Plain) return sidecarText;

        string? tagText = await Task.Run(() => ReadTagComment(confirmedAudioPath), cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(tagText)
            ? LyricsDocument.Empty
            : new LyricsDocument(LyricsKind.Plain, Array.Empty<LyricLine>(), tagText.Trim(), "Текст из тега");
    }

    public static async Task SavePastedLyricsAsync(string audioPath, string text, CancellationToken cancellationToken = default)
    {
        string normalized = text.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return;
        if (Encoding.UTF8.GetByteCount(normalized) > MaxCachedLyricsBytes)
            throw new InvalidDataException("Скопированный текст больше 2 МБ.");

        Directory.CreateDirectory(PastedLyricsCacheDirectory);
        string cachePath = GetPastedLyricsCachePath(audioPath);
        string temporaryPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, normalized + Environment.NewLine, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
            PrunePastedLyricsCache();
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Кэш не должен превращать успешное сохранение текста в ошибку очистки.
            }
        }
    }

    // Вызывается со страницы «Профиль»: показывает фактический размер уже после удаления
    // просроченных/слишком больших записей. Никаких обращений к сети и путей аудиофайлов нет.
    public static LyricsCacheInfo GetPastedLyricsCacheInfo()
    {
        lock (CacheMaintenanceGate)
        {
            PrunePastedLyricsCacheUnsafe();
            return GetPastedLyricsCacheInfoUnsafe();
        }
    }

    // Удаляет только собственную папку Lumisense с хешированными текстами. .lrc/.txt рядом с
    // аудиофайлами и тексты в тегах никогда не считаются кэшем и этим действием не затрагиваются.
    public static bool ClearPastedLyricsCache()
    {
        lock (CacheMaintenanceGate)
        {
            try
            {
                if (!Directory.Exists(PastedLyricsCacheDirectory)) return true;
                Directory.Delete(PastedLyricsCacheDirectory, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Не удалось очистить кэш текста песен: {ex.Message}");
                return false;
            }
        }
    }

    // Режим "Plain" показывает синхронный текст как обычный: строки без меток времени одним блоком. Остальные документы
    // (обычный текст, пустой) и режим "Synced" возвращаются как есть.
    public static LyricsDocument ApplyDisplayMode(LyricsDocument document, string? displayMode)
    {
        if (document.Kind != LyricsKind.Synced || displayMode != "Plain") return document;

        string text = string.Join(Environment.NewLine, document.Lines.Select(line => line.Text)).Trim();
        return text.Length == 0
            ? document
            : new LyricsDocument(LyricsKind.Plain, Array.Empty<LyricLine>(), text, PlainSourceLabel(document.SourceLabel));
    }

    // Подпись источника не должна называть текст синхронным, когда он показан обычным.
    private static string PlainSourceLabel(string label)
    {
        const string syncedSuffix = " · синхронный текст";
        if (label == "Синхронный LRC") return "Текст из LRC";
        return label.EndsWith(syncedSuffix, StringComparison.Ordinal)
            ? label[..^syncedSuffix.Length] + " · текст"
            : label;
    }

    private static async Task<LyricsDocument> LoadPlainTextAsync(string textPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(textPath)) return LyricsDocument.Empty;

            string plain = await File.ReadAllTextAsync(textPath, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(plain)
                ? LyricsDocument.Empty
                : new LyricsDocument(LyricsKind.Plain, Array.Empty<LyricLine>(), plain.Trim(), "Текстовый файл");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Повреждённый пользовательский TXT не мешает fallback к тегу.
            return LyricsDocument.Empty;
        }
    }

    private static async Task<LyricsDocument> LoadSyncedLrcAsync(string lrcPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(lrcPath)) return LyricsDocument.Empty;

            string lrc = await File.ReadAllTextAsync(lrcPath, cancellationToken).ConfigureAwait(false);
            List<LyricLine> lines = ParseLrc(lrc);
            return lines.Count > 0
                ? new LyricsDocument(LyricsKind.Synced, lines, string.Empty, "Синхронный LRC")
                : LyricsDocument.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Повреждённый LRC не должен мешать fallback к соседнему LRC, тексту или тегу.
            return LyricsDocument.Empty;
        }
    }

    private static async Task WriteTextAtomicallyAsync(string destination, string content, CancellationToken cancellationToken)
    {
        string temporaryPath = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Временный файл будет убран при следующем обслуживании папки или вручную.
            }
        }
    }

    private static async Task<LyricsDocument> LoadPastedLyricsCacheAsync(string audioPath, CancellationToken cancellationToken)
    {
        string cachePath = GetPastedLyricsCachePath(audioPath);
        if (!File.Exists(cachePath))
        {
            string legacyPath = GetLegacyPastedLyricsCachePath(audioPath);
            if (!File.Exists(legacyPath)) return LyricsDocument.Empty;
            cachePath = legacyPath;
        }

        try
        {

            var info = new FileInfo(cachePath);
            if (info.Length <= 0 || info.Length > MaxCachedLyricsBytes ||
                info.LastWriteTimeUtc < DateTime.UtcNow - CachedLyricsRetention)
            {
                TryDeleteCacheFile(cachePath);
                return LyricsDocument.Empty;
            }

            string text = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return LyricsDocument.Empty;

            // LastWriteTime служит LRU-меткой. Одно лёгкое обновление позволяет не удалить
            // текст часто слушаемого трека раньше редких старых записей при лимите размера.
            try { File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warn($"Не удалось обновить время кэша текста: {ex.Message}"); }

            return new LyricsDocument(LyricsKind.Plain, Array.Empty<LyricLine>(), text.Trim(), "Кэш вставленного текста");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LyricsDocument.Empty;
        }
    }

    private static void PrunePastedLyricsCache()
    {
        lock (CacheMaintenanceGate)
            PrunePastedLyricsCacheUnsafe();
    }

    private static void PrunePastedLyricsCacheUnsafe()
    {
        try
        {
            if (!Directory.Exists(PastedLyricsCacheDirectory)) return;

            DateTime expiry = DateTime.UtcNow - CachedLyricsRetention;
            var entries = new List<FileInfo>();
            foreach (string path in Directory.EnumerateFiles(PastedLyricsCacheDirectory, "*.txt", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaxCachedLyricsBytes || info.LastWriteTimeUtc < expiry)
                {
                    TryDeleteCacheFile(path);
                    continue;
                }

                entries.Add(info);
            }

            // Прерванная атомарная запись не должна занимать место бесконечно.
            foreach (string temporaryPath in Directory.EnumerateFiles(PastedLyricsCacheDirectory, "*.tmp-*", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(temporaryPath) < DateTime.UtcNow - TimeSpan.FromDays(1))
                    TryDeleteCacheFile(temporaryPath);
            }

            long totalBytes = entries.Sum(entry => entry.Length);
            foreach (FileInfo entry in entries.OrderBy(entry => entry.LastWriteTimeUtc).ThenBy(entry => entry.Name).ToList())
            {
                if (entries.Count <= MaxCachedLyricsEntries && totalBytes <= MaxTotalCachedLyricsBytes) break;
                long length = entry.Length;
                if (TryDeleteCacheFile(entry.FullName))
                {
                    entries.Remove(entry);
                    totalBytes -= length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось обслужить кэш текста песен: {ex.Message}");
        }
    }

    private static LyricsCacheInfo GetPastedLyricsCacheInfoUnsafe()
    {
        try
        {
            var entries = Directory.Exists(PastedLyricsCacheDirectory)
                ? Directory.EnumerateFiles(PastedLyricsCacheDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(info => info.Exists && info.Length > 0 && info.Length <= MaxCachedLyricsBytes)
                    .ToList()
                : new List<FileInfo>();
            return new LyricsCacheInfo(entries.Count, entries.Sum(entry => entry.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось получить размер кэша текста песен: {ex.Message}");
            return default;
        }
    }

    private static bool TryDeleteCacheFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось удалить файл кэша текста: {ex.Message}");
            return false;
        }
    }

    private static string GetPastedLyricsCachePath(string audioPath) => GetManagedTextPath(audioPath);

    internal static string GetManagedTextPath(string audioPath) =>
        Path.Combine(PastedLyricsCacheDirectory, GetAudioPathCacheKey(audioPath) + ".txt");

    private static string GetLegacyPastedLyricsCachePath(string audioPath) =>
        Path.Combine(LegacyPastedLyricsCacheDirectory, GetAudioPathCacheKey(audioPath) + ".txt");

    internal static string GetManagedLrcPath(string audioPath) =>
        Path.Combine(ManagedLrcDirectory, GetAudioPathCacheKey(audioPath) + ".lrc");

    private static string GetAudioPathCacheKey(string audioPath)
    {
        string canonicalPath = Path.GetFullPath(audioPath).Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath))).ToLowerInvariant();
    }

    // Включает дополнительный источник текстов (NetEase через Lyricify); выставляется из настроек.
    public static bool ExtraSourceEnabled { get; set; }

    // Встроенный поиск по LRCLIB. Никакой ключ не нужен, но сервис просит корректный User-Agent
    // и последовательные запросы; UI отменяет предыдущий поиск до начала следующего.
    private static async Task<IReadOnlyList<OnlineLyricsResult>> SearchLrcLibAsync(
        string trackName,
        string artistName,
        CancellationToken cancellationToken,
        bool generalQuery = false)
    {
        string title = trackName.Trim();
        string artist = artistName.Trim();
        if (string.IsNullOrWhiteSpace(title) || title == "Файл не выбран")
            return Array.Empty<OnlineLyricsResult>();

        // Общий запрос (q) ищет по названию и исполнителю сразу: помогает, когда теги записаны не так, как в каталоге LRCLIB.
        var query = new List<string> { generalQuery ? $"q={Uri.EscapeDataString(title)}" : $"track_name={Uri.EscapeDataString(title)}" };
        if (!generalQuery && !string.IsNullOrWhiteSpace(artist) && artist != "—")
            query.Add($"artist_name={Uri.EscapeDataString(artist)}");

        string url = LrcLibSearchEndpoint + "?" + string.Join("&", query);

        // 429 и сетевые сбои — обычное дело для публичного сервиса: повторяем запрос с паузой, не удерживая SearchGate,
        // чтобы не блокировать остальные поиски, и только после исчерпания попыток сообщаем об ошибке.
        for (int attempt = 1; ; attempt++)
        {
            bool rateLimited = false;
            TimeSpan? retryAfter = null;

            await SearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    rateLimited = true;
                    retryAfter = response.Headers.RetryAfter?.Delta;
                }
                else
                {
                    if (!response.IsSuccessStatusCode)
                        return Array.Empty<OnlineLyricsResult>();

                    if (response.Content.Headers.ContentLength is long length && length > MaxSearchResponseBytes)
                        return Array.Empty<OnlineLyricsResult>();

                    string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (json.Length > MaxSearchResponseBytes)
                        return Array.Empty<OnlineLyricsResult>();

                    var records = JsonSerializer.Deserialize<List<LrcLibRecord>>(json, JsonOptions) ?? new List<LrcLibRecord>();
                    return records
                        .Where(record => !string.IsNullOrWhiteSpace(record.TrackName) &&
                                         (!string.IsNullOrWhiteSpace(record.SyncedLyrics) || !string.IsNullOrWhiteSpace(record.PlainLyrics)))
                        .Take(20)
                        .Select(record => new OnlineLyricsResult(
                            record.Id,
                            record.TrackName ?? string.Empty,
                            record.ArtistName ?? string.Empty,
                            record.AlbumName ?? string.Empty,
                            record.Duration,
                            record.PlainLyrics,
                            record.SyncedLyrics))
                        .ToList();
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
                return Array.Empty<OnlineLyricsResult>();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Таймаут HttpClient, а не отмена пользователем.
            }
            finally
            {
                SearchGate.Release();
            }

            int maxAttempts = rateLimited ? MaxLrcLibRateLimitAttempts : MaxLrcLibTransientAttempts;
            if (attempt >= maxAttempts)
            {
                if (rateLimited) throw new LyricsRateLimitException(retryAfter);
                throw new LrcLibUnavailableException();
            }

            TimeSpan delay = rateLimited
                ? TimeSpan.FromSeconds(Math.Clamp((retryAfter ?? TimeSpan.FromSeconds(2 * attempt)).TotalSeconds, 1, 6))
                : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    // Теги исполнителя и названия часто записаны по-разному: пробуем до трёх вариантов и останавливаемся
    // на первом непустом результате, чтобы не нагружать публичный сервис.
    public static async Task<IReadOnlyList<OnlineLyricsResult>> SearchOnlineVariantsAsync(
        string trackName,
        string artistName,
        CancellationToken cancellationToken)
    {
        // Варианты: как в теге, без пометок версии, с первым исполнителем, только название и, наконец, общий запрос.
        // Сначала только LRCLIB: он быстрый. Дополнительные источники опрашиваются один раз в конце и дописываются к выдаче,
        // а не повторяются на каждом варианте запроса.
        IReadOnlyList<OnlineLyricsResult> primary = Array.Empty<OnlineLyricsResult>();
        LyricsRateLimitException? rateLimit = null;
        try
        {
            foreach ((string title, string artist) in LyricsMatcher.BuildQueryVariants(trackName, artistName))
            {
                primary = await SearchLrcLibAsync(title, artist, cancellationToken);
                if (primary.Count > 0) break;
            }

            string? generalQuery = BuildGeneralQuery(trackName, artistName);
            if (primary.Count == 0 && generalQuery is not null)
                primary = await SearchLrcLibAsync(generalQuery, string.Empty, cancellationToken, generalQuery: true);
        }
        catch (LyricsRateLimitException ex)
        {
            // Недоступность или лимит LRCLIB не должны отменять поиск в остальных источниках.
            rateLimit = ex;
        }
        catch (LrcLibUnavailableException)
        {
        }

        IReadOnlyList<OnlineLyricsResult> extra = Array.Empty<OnlineLyricsResult>();
        if (ExtraSourceEnabled)
        {
            (string extraTitle, string extraArtist) = ExtraSourceQuery(trackName, artistName);
            extra = await LyricifyLyricsSource.SearchAsync(extraTitle, extraArtist, cancellationToken);
        }

        if (primary.Count == 0 && extra.Count == 0 && rateLimit is not null) throw rateLimit;
        return extra.Count == 0 ? primary : primary.Concat(extra).ToList();
    }

    // Запрос для дополнительных источников: название без пометок версии и исполнитель как в теге (их поиск нечёткий).
    private static (string Title, string Artist) ExtraSourceQuery(string trackName, string artistName)
    {
        string artist = artistName.Trim();
        return (LyricsMatcher.CleanTitle(trackName), artist == "—" ? string.Empty : artist);
    }

    // Результат автоподбора: уверенное совпадение (или null), сколько кандидатов нашлось вообще — от этого зависит подсказка
    // «Нужен выбор варианта» или «Нет текста» — и все уверенные совпадения по убыванию качества (для кнопки «Другой вариант»).
    public sealed record AutomaticLyricsLookup(OnlineLyricsResult? Best, int CandidateCount, IReadOnlyList<OnlineLyricsResult> Matches);

    // Автоподбор текста для текущего трека: перебирает варианты запроса, пока не появится уверенное совпадение
    // (см. LyricsMatcher.RankMatches). Останавливается на первом удачном варианте, чтобы не нагружать публичные сервисы.
    public static async Task<AutomaticLyricsLookup> FindAutomaticAsync(
        string trackName,
        string artistName,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var candidates = new List<OnlineLyricsResult>();

        // 1) LRCLIB по всем вариантам запроса — быстро и без внешних китайских серверов.
        (IReadOnlyList<OnlineLyricsResult> matches, LyricsRateLimitException? rateLimit) = await CollectLrcLibMatchesAsync(
            candidates, trackName, artistName, durationSeconds, cancellationToken);

        // 2) Дополнительные источники — только если LRCLIB не дал уверенного совпадения и они включены; один запрос с таймаутом.
        if (matches.Count == 0 && ExtraSourceEnabled)
        {
            (string extraTitle, string extraArtist) = ExtraSourceQuery(trackName, artistName);
            AddCandidates(candidates, await LyricifyLyricsSource.SearchAsync(extraTitle, extraArtist, cancellationToken));
            matches = LyricsMatcher.RankMatches(candidates, trackName, artistName, durationSeconds);
        }

        // Лимит сообщаем, только если ни один источник ничего не дал: иначе результат уже есть.
        if (candidates.Count == 0 && rateLimit is not null) throw rateLimit;

        return new AutomaticLyricsLookup(matches.FirstOrDefault(), candidates.Count, matches);
    }

    // Варианты текста из LRCLIB для кнопки «Другой вариант», когда автоподбор их не запоминал (текст был сохранён раньше).
    public static async Task<IReadOnlyList<OnlineLyricsResult>> FindLrcLibVariantsAsync(
        string trackName,
        string artistName,
        double durationSeconds,
        CancellationToken cancellationToken) =>
        await FindLrcLibVariantsCoreAsync(trackName, artistName, durationSeconds, cancellationToken);

    private static async Task<IReadOnlyList<OnlineLyricsResult>> FindLrcLibVariantsCoreAsync(
        string trackName,
        string artistName,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var candidates = new List<OnlineLyricsResult>();
        (IReadOnlyList<OnlineLyricsResult> matches, LyricsRateLimitException? rateLimit) = await CollectLrcLibMatchesAsync(
            candidates, trackName, artistName, durationSeconds, cancellationToken);
        if (candidates.Count == 0 && rateLimit is not null) throw rateLimit;
        return matches;
    }

    // Варианты из дополнительных источников (NetEase, QQ Music); пусто, если источник выключен. Запрашиваются по требованию:
    // обычно хватает LRCLIB, а китайские серверы медленнее.
    public static async Task<IReadOnlyList<OnlineLyricsResult>> FindExtraVariantsAsync(
        string trackName,
        string artistName,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        if (!ExtraSourceEnabled) return Array.Empty<OnlineLyricsResult>();

        (string extraTitle, string extraArtist) = ExtraSourceQuery(trackName, artistName);
        IReadOnlyList<OnlineLyricsResult> found = await LyricifyLyricsSource.SearchAsync(extraTitle, extraArtist, cancellationToken);
        return LyricsMatcher.RankMatches(found, trackName, artistName, durationSeconds);
    }

    // LRCLIB по вариантам запроса и общему запросу; останавливается, когда появились уверенные совпадения.
    // Лимит или недоступность LRCLIB не бросаются наружу: вызывающий код продолжит с другими источниками,
    // а лимит получит отдельным значением, чтобы показать его, только если текст так и не найден.
    private static async Task<(IReadOnlyList<OnlineLyricsResult> Matches, LyricsRateLimitException? RateLimit)> CollectLrcLibMatchesAsync(
        List<OnlineLyricsResult> candidates,
        string trackName,
        string artistName,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach ((string title, string artist) in LyricsMatcher.BuildQueryVariants(trackName, artistName))
            {
                AddCandidates(candidates, await SearchLrcLibAsync(title, artist, cancellationToken));
                IReadOnlyList<OnlineLyricsResult> matches = LyricsMatcher.RankMatches(candidates, trackName, artistName, durationSeconds);
                if (matches.Count > 0) return (matches, null);
            }

            string? generalQuery = BuildGeneralQuery(trackName, artistName);
            if (generalQuery is not null)
            {
                AddCandidates(candidates, await SearchLrcLibAsync(generalQuery, string.Empty, cancellationToken, generalQuery: true));
                return (LyricsMatcher.RankMatches(candidates, trackName, artistName, durationSeconds), null);
            }
        }
        catch (LyricsRateLimitException ex)
        {
            return (LyricsMatcher.RankMatches(candidates, trackName, artistName, durationSeconds), ex);
        }
        catch (LrcLibUnavailableException)
        {
            return (LyricsMatcher.RankMatches(candidates, trackName, artistName, durationSeconds), null);
        }

        return (Array.Empty<OnlineLyricsResult>(), null);
    }

    private static void AddCandidates(List<OnlineLyricsResult> candidates, IReadOnlyList<OnlineLyricsResult> found)
    {
        foreach (OnlineLyricsResult result in found)
        {
            if (!candidates.Any(existing => existing.Source == result.Source && existing.Id == result.Id))
                candidates.Add(result);
        }
    }

    // «Исполнитель название» без пометок версии; null, если исполнитель неизвестен (тогда общий запрос ничего не добавляет).
    private static string? BuildGeneralQuery(string trackName, string artistName)
    {
        string artist = LyricsMatcher.FirstArtist(artistName);
        string title = LyricsMatcher.CleanTitle(trackName);
        return artist.Length == 0 || title.Length == 0 ? null : $"{artist} {title}";
    }

    private static string LabelSource(OnlineLyricsResult result) => result.Source;

    public static LyricsDocument CreateDocumentFromOnlineResult(OnlineLyricsResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.SyncedLyrics))
        {
            List<LyricLine> lines = ParseLrc(result.SyncedLyrics);
            if (lines.Count > 0)
                return new LyricsDocument(LyricsKind.Synced, lines, string.Empty, $"{LabelSource(result)} · синхронный текст");
        }

        return string.IsNullOrWhiteSpace(result.PlainLyrics)
            ? LyricsDocument.Empty
            : new LyricsDocument(LyricsKind.Plain, Array.Empty<LyricLine>(), result.PlainLyrics.Trim(), $"{LabelSource(result)} · текст");
    }

    public static async Task SaveOnlineResultAsync(string audioPath, OnlineLyricsResult result, CancellationToken cancellationToken)
    {
        LyricsDocument document = CreateDocumentFromOnlineResult(result);
        if (document.Kind == LyricsKind.None) return;

        if (document.Kind == LyricsKind.Synced)
        {
            Directory.CreateDirectory(ManagedLrcDirectory);
            string destination = GetManagedLrcPath(audioPath);
            await WriteTextAtomicallyAsync(destination, result.SyncedLyrics!.Trim() + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Управляемый LRC читается раньше обычного текста: без удаления выбранный простой текст остался бы скрыт прежним синхронным.
        try
        {
            string managedLrc = GetManagedLrcPath(audioPath);
            if (File.Exists(managedLrc)) File.Delete(managedLrc);
        }
        catch (IOException)
        {
            // Не получилось убрать старый LRC: обычный текст всё равно сохраняется ниже.
        }
        catch (UnauthorizedAccessException)
        {
        }

        // Обычный текст сохраняем в управляемой txt-папке, а не рядом с аудиофайлом.
        await SavePastedLyricsAsync(audioPath, document.PlainText, cancellationToken).ConfigureAwait(false);
    }

    public static int FindActiveLineIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int low = 0;
        int high = lines.Count - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (lines[mid].Time <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private static List<LyricLine> ParseLrc(string content)
    {
        var result = new List<LyricLine>();
        int offsetMilliseconds = 0;

        foreach (string rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            Match offsetMatch = OffsetRegex.Match(line);
            if (offsetMatch.Success && int.TryParse(offsetMatch.Groups["milliseconds"].Value, out int parsedOffset))
            {
                offsetMilliseconds = parsedOffset;
                continue;
            }

            MatchCollection timestamps = TimestampRegex.Matches(line);
            if (timestamps.Count == 0) continue;

            string text = TimestampRegex.Replace(line, string.Empty).Trim();
            foreach (Match timestamp in timestamps)
            {
                if (!int.TryParse(timestamp.Groups["minutes"].Value, out int minutes) ||
                    !int.TryParse(timestamp.Groups["seconds"].Value, out int seconds))
                    continue;

                int fractionMilliseconds = ParseFractionMilliseconds(timestamp.Groups["fraction"].Value);
                double totalMilliseconds = TimeSpan.FromMinutes(minutes).TotalMilliseconds +
                                           TimeSpan.FromSeconds(seconds).TotalMilliseconds +
                                           fractionMilliseconds + offsetMilliseconds;
                if (totalMilliseconds < 0) continue;

                result.Add(new LyricLine(TimeSpan.FromMilliseconds(totalMilliseconds), text));
            }
        }

        return result.OrderBy(line => line.Time).ToList();
    }

    private static int ParseFractionMilliseconds(string fraction)
    {
        if (string.IsNullOrWhiteSpace(fraction)) return 0;
        return fraction.Length switch
        {
            1 => int.TryParse(fraction, out int tenths) ? tenths * 100 : 0,
            2 => int.TryParse(fraction, out int hundredths) ? hundredths * 10 : 0,
            _ => int.TryParse(fraction[..3], out int milliseconds) ? milliseconds : 0
        };
    }

    private static string? ReadTagComment(string audioPath)
    {
        try
        {
            var track = new ATL.Track(audioPath);
            return track.Comment;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        LyricsNetworkIdentity.Apply(client);
        return client;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class LrcLibRecord
    {
        public long Id { get; init; }
        public string? TrackName { get; init; }
        public string? ArtistName { get; init; }
        public string? AlbumName { get; init; }
        public double Duration { get; init; }
        public string? PlainLyrics { get; init; }
        public string? SyncedLyrics { get; init; }
    }
}

public sealed class LyricsRateLimitException(TimeSpan? retryAfter) : Exception
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// Внутренний признак: LRCLIB не ответил после повторов. Не показывается пользователю, а лишь переключает поиск на другие источники.
internal sealed class LrcLibUnavailableException : Exception;
