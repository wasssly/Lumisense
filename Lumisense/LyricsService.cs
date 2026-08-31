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
    string? SyncedLyrics)
{
    public bool HasSyncedLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool HasLyrics => HasSyncedLyrics || !string.IsNullOrWhiteSpace(PlainLyrics);
    public string DisplayName => string.IsNullOrWhiteSpace(ArtistName)
        ? TrackName
        : $"{ArtistName} — {TrackName}";
}

// Локальная загрузка текстов и встроенный поиск через LRCLIB.
// Now Playing может выполнить один автоматический запрос после отсутствующего локального текста,
// но сохраняет результат только при точном совпадении названия и исполнителя. Неоднозначные
// варианты пользователь выбирает вручную через встроенную панель поиска.
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

        // Сначала используем LRC, который Lumisense сохранил в собственной папке. Так
        // автоматический онлайн-поиск не создаёт .lrc рядом с аудиофайлом. Затем сохраняем
        // совместимость с LRC, который пользователь положил рядом с треком вручную.
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

    // Встроенный поиск по LRCLIB. Никакой ключ не нужен, но сервис просит корректный User-Agent
    // и последовательные запросы; UI отменяет предыдущий поиск до начала следующего.
    public static async Task<IReadOnlyList<OnlineLyricsResult>> SearchOnlineAsync(
        string trackName,
        string artistName,
        CancellationToken cancellationToken)
    {
        string title = trackName.Trim();
        string artist = artistName.Trim();
        if (string.IsNullOrWhiteSpace(title) || title == "Файл не выбран")
            return Array.Empty<OnlineLyricsResult>();

        var query = new List<string> { $"track_name={Uri.EscapeDataString(title)}" };
        if (!string.IsNullOrWhiteSpace(artist) && artist != "—")
            query.Add($"artist_name={Uri.EscapeDataString(artist)}");

        await SearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LrcLibSearchEndpoint + "?" + string.Join("&", query));
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                throw new LyricsRateLimitException(retryAfter);
            }

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
        finally
        {
            SearchGate.Release();
        }
    }

    // Ручной поиск может столкнуться с неточными тегами: «исполнитель» и «название» часто
    // записаны по-разному. Пробуем не более трёх последовательных вариантов и прекращаемся
    // сразу после первого непустого набора, не создавая лишнюю нагрузку на публичный сервис.
    public static async Task<IReadOnlyList<OnlineLyricsResult>> SearchOnlineVariantsAsync(
        string trackName,
        string artistName,
        CancellationToken cancellationToken)
    {
        string title = trackName.Trim();
        string artist = artistName.Trim();
        IReadOnlyList<OnlineLyricsResult> primary = await SearchOnlineAsync(title, artist, cancellationToken);
        if (primary.Count > 0 || string.IsNullOrWhiteSpace(artist)) return primary;

        IReadOnlyList<OnlineLyricsResult> titleOnly = await SearchOnlineAsync(title, string.Empty, cancellationToken);
        if (titleOnly.Count > 0) return titleOnly;

        // Последний вариант помогает, когда каталог хранит исполнителя в самом названии.
        return await SearchOnlineAsync($"{artist} {title}", string.Empty, cancellationToken);
    }

    public static LyricsDocument CreateDocumentFromOnlineResult(OnlineLyricsResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.SyncedLyrics))
        {
            List<LyricLine> lines = ParseLrc(result.SyncedLyrics);
            if (lines.Count > 0)
                return new LyricsDocument(LyricsKind.Synced, lines, string.Empty, "LRCLIB · синхронный текст");
        }

        return string.IsNullOrWhiteSpace(result.PlainLyrics)
            ? LyricsDocument.Empty
            : new LyricsDocument(LyricsKind.Plain, Array.Empty<LyricLine>(), result.PlainLyrics.Trim(), "LRCLIB · текст");
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
            using var file = TagLib.File.Create(audioPath);
            return file.Tag.Comment;
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
