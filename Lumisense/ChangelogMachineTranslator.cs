using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Lumisense;

// Автоматический перевод журнала изменений на английский (см. ChangelogTranslationCatalog). Используется неофициальный интерфейс Google Translate без ключа (translate.googleapis.com,
// client=gtx): он бывает недоступен и ограничивает частоту запросов, поэтому любой сбой оставляет русский текст, а при отказе
// сервиса запросы приостанавливаются на BlockDuration.
//
// Работает только при включённой настройке AppSettings.ChangelogAutoTranslate (по умолчанию выключена). Наружу уходит только текст
// пункта журнала изменений: это публичный текст, данных пользователя в нём нет. Результаты хранятся в
// %AppData%\Lumisense\changelog-translations.json и повторно не запрашиваются.
internal sealed class ChangelogMachineTranslator
{
    internal const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    internal const int MaxCacheEntries = 2000;
    internal const int MaxSourceLength = 450;
    private const int MaxPerRun = 600;
    private const int ProgressEvery = 10;

    // Пауза между запросами: сервис ограничивает частоту, а при частых запросах подряд быстрее отвечает отказом (429).
    private static readonly TimeSpan DefaultRequestDelay = TimeSpan.FromMilliseconds(100);
    private const int DefaultParallelism = 4;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(30);

    private static readonly Lazy<ChangelogMachineTranslator> SharedInstance = new(() => new ChangelogMachineTranslator(
        () => CreateDefaultClient(), DefaultCachePath));

    public static ChangelogMachineTranslator Shared => SharedInstance.Value;

    internal static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense", "changelog-translations.json");

    private readonly Lazy<HttpClient> _http;
    private readonly string _cachePath;
    private readonly TimeSpan _requestDelay;
    private readonly int _maxParallel;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private Dictionary<string, string>? _cache;
    private DateTime _blockedUntilUtc = DateTime.MinValue;

    internal ChangelogMachineTranslator(Func<HttpClient> httpFactory, string cachePath, TimeSpan? requestDelay = null, int maxParallel = DefaultParallelism)
    {
        _http = new Lazy<HttpClient>(httpFactory);
        _cachePath = cachePath;
        _requestDelay = requestDelay ?? DefaultRequestDelay;
        _maxParallel = Math.Max(1, maxParallel);
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        LyricsNetworkIdentity.Apply(client);
        return client;
    }

    public bool TryGetCached(string source, out string translated)
    {
        lock (_gate)
        {
            EnsureCacheLoaded();
            return _cache!.TryGetValue(source, out translated!);
        }
    }

    // Переводит пункты, которых ещё нет в кэше, в порядке списка (в несколько потоков, поэтому порядок готовых результатов
    // приблизительный). onProgress вызывается каждые ProgressEvery переводов и в конце, если что-то перевелось (из любого
    // потока). Возвращает число новых переводов. Одновременно идёт только один прогон: новый ждёт завершения отменённого предыдущего.
    public async Task<int> TranslateAsync(IEnumerable<string> sources, Action? onProgress, CancellationToken token)
    {
        if (DateTime.UtcNow < _blockedUntilUtc) return 0;
        if (!await _runLock.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false)) return 0;

        int translated = 0;
        try
        {
            List<string> pending;
            lock (_gate)
            {
                EnsureCacheLoaded();
                pending = sources
                    .Where(text => IsTranslatable(text) && !_cache!.ContainsKey(text))
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxPerRun)
                    .ToList();
            }

            // stop останавливает остальные запросы, когда сервис отказал или кэш заполнен; токен вызывающего отменяет всё.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallel, CancellationToken = stop.Token };
            try
            {
                await Parallel.ForEachAsync(pending, options, async (text, ct) =>
                {
                    if (_requestDelay > TimeSpan.Zero) await Task.Delay(_requestDelay, ct).ConfigureAwait(false);

                    RequestResult result = await RequestAsync(text, ct).ConfigureAwait(false);
                    if (result.Blocked)
                    {
                        _blockedUntilUtc = DateTime.UtcNow + BlockDuration;
                        Logger.Info($"Автоперевод журнала изменений приостановлен: {result.Reason}");
                        stop.Cancel();
                        return;
                    }

                    if (result.Translation is null) return;

                    lock (_gate)
                    {
                        if (_cache!.Count >= MaxCacheEntries)
                        {
                            stop.Cancel();
                            return;
                        }

                        _cache[text] = result.Translation;
                    }

                    int done = Interlocked.Increment(ref translated);
                    if (done % ProgressEvery == 0)
                    {
                        SaveCache();
                        onProgress?.Invoke();
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Остановка изнутри (отказ сервиса или переполненный кэш): уже полученное сохраняется ниже.
            }

            if (translated > 0)
            {
                SaveCache();
                onProgress?.Invoke();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (translated > 0) SaveCache();
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Автоперевод журнала изменений остановлен: {ex.Message}");
            if (translated > 0) SaveCache();
        }
        finally
        {
            _runLock.Release();
        }

        return translated;
    }

    private readonly record struct RequestResult(string? Translation, bool Blocked, string Reason);

    private async Task<RequestResult> RequestAsync(string text, CancellationToken token)
    {
        string url = $"{Endpoint}?client=gtx&sl=ru&tl=en&dt=t&q={Uri.EscapeDataString(text)}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpResponseMessage response = await _http.Value.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden || (int)response.StatusCode >= 500)
                return new RequestResult(null, true, $"ответ {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                return new RequestResult(null, false, $"ответ {(int)response.StatusCode}");

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            string? translated = ParseResponse(body);
            return IsPlausible(text, translated)
                ? new RequestResult(translated, false, "")
                : new RequestResult(null, false, "перевод отклонён проверкой");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new RequestResult(null, true, "сервис не ответил вовремя");
        }
        catch (HttpRequestException ex)
        {
            return new RequestResult(null, true, ex.Message);
        }
    }

    // Ответ: [[["перевод 1","оригинал 1",...],["перевод 2","оригинал 2",...]],null,"ru",...] — предложения идут отдельными сегментами.
    internal static string? ParseResponse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;

            JsonElement segments = root[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            var builder = new StringBuilder();
            foreach (JsonElement segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0) continue;
                if (segment[0].ValueKind == JsonValueKind.String) builder.Append(segment[0].GetString());
            }

            string result = builder.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Перевод принимается, если он не пустой, не совпадает с оригиналом, написан в основном латиницей и не раздут.
    internal static bool IsPlausible(string source, string? translated)
    {
        if (string.IsNullOrWhiteSpace(translated) || string.Equals(source, translated, StringComparison.Ordinal)) return false;
        if (translated.Length > source.Length * 4 + 40) return false;

        int latin = 0, cyrillic = 0;
        foreach (char symbol in translated)
        {
            if (symbol is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
            else if (IsCyrillic(symbol)) cyrillic++;
        }

        return latin > 0 && cyrillic <= (latin + cyrillic) * 0.3;
    }

    // Переводятся только русские пункты разумной длины.
    internal static bool IsTranslatable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= MaxSourceLength && text.Any(IsCyrillic);

    private static bool IsCyrillic(char symbol) => symbol is >= '\u0400' and <= '\u04FF';

    private void EnsureCacheLoaded()
    {
        if (_cache is not null) return;

        _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_cachePath)) return;

            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cachePath, Encoding.UTF8));
            if (stored is null) return;

            foreach ((string source, string translation) in stored)
            {
                if (_cache.Count >= MaxCacheEntries) break;
                if (IsTranslatable(source) && IsPlausible(source, translation)) _cache[source] = translation;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённый или недоступный кэш не критичен: пункты просто переведутся заново.
            Logger.Info($"Кэш автоперевода журнала изменений не прочитан: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            Dictionary<string, string> snapshot;
            lock (_gate) snapshot = new Dictionary<string, string>(_cache!, StringComparer.Ordinal);

            string? directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Запись идёт из нескольких потоков перевода: общий временный файл нельзя писать одновременно.
            lock (_saveGate)
            {
                string temp = _cachePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
                File.Move(temp, _cachePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Info($"Кэш автоперевода журнала изменений не сохранён: {ex.Message}");
        }
    }
}
