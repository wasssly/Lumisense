using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace Lumisense;

// Поиск обложки трека в интернете по исполнителю и названию через два открытых API без
// ключа: iTunes Search и Deezer, запросы параллельно, результаты объединяются в один список.
// Один iTunes нередко не находил обложки исполнителей вне основного каталога (русские/СНГ
// и другие локальные артисты) — каталог Deezer пересекается, но не идентичен, вместе
// закрывают больше запросов. Если один источник недоступен, второй всё равно отвечает.
//
// Показывает варианты миниатюрами; при выборе скачивает изображение в повышенном разрешении
// и возвращает его TrackTagsWindow — та сохраняет так же, как обложку с диска.
//
// Genius не подключен: их API отдаёт обложки только вместе с текстами песен и требует
// личный Client Access Token — без ключа от пользователя не заработает.
public partial class CoverArtSearchWindow : FluentWindow
{
    private const int MaxApiJsonBytes = 2 * 1024 * 1024;
    private const int MaxImageBytes = 10 * 1024 * 1024;

    // Обложки из поиска сохраняются в %AppData%\\Lumisense\\cover-cache. URL не попадает
    // в имя файла: SHA-256 даёт короткий детерминированный ключ и исключает path traversal.
    // Лимиты не позволяют кэшу незаметно занимать неограниченное место на диске.
    private const int ArtworkCacheMaxFiles = 256;
    private const long ArtworkCacheMaxBytes = 128L * 1024 * 1024;
    private static readonly string ArtworkCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense", "cover-cache");

    private static readonly HashSet<string> TrustedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mzstatic.com", "apple.com", "deezer.com", "dzcdn.net"
    };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Результат ручной очистки для интерфейса настроек. FailedFiles > 0 обычно означает,
    // что параллельно с очисткой какой-то файл кэша был занят новой загрузкой.
    public readonly record struct ArtworkCacheClearResult(int DeletedFiles, long FreedBytes, int FailedFiles);

    // Заполняется только если пользователь кликнул по одному из найденных вариантов —
    // при закрытии окна без выбора (Escape/крестик/"Закрыть") остаётся null.
    public byte[]? SelectedImageBytes { get; private set; }
    public string? SelectedImageMimeType { get; private set; }

    // Единая карточка результата независимо от источника: у iTunes полноразмерная обложка
    // получается подстановкой размера в тот же URL миниатюры (см. WithArtworkSize), а у
    // Deezer это в принципе отдельный URL (cover_medium/cover_xl) — поэтому модель хранит оба
    // адреса сразу, а не пытается вывести один из другого.
    private readonly record struct ArtResult(string ThumbUrl, string FullUrl, string Label);

    // Отменяет предыдущий незавершённый поиск (и все ещё летящие по нему запросы миниатюр),
    // когда пользователь запускает новый поиск или явно нажимает "Отмена". Без него смена
    // запроса на середине загрузки миниатюр оставляла бы гоняться по сети старые, уже
    // никому не нужные запросы.
    private CancellationTokenSource? _searchCts;

    public CoverArtSearchWindow(string? artist, string? title, AppSettings? settings = null)
    {
        InitializeComponent();
        if (settings != null)
            AccessibilityPreferences.ApplyToWindow(this, settings);

        var query = string.Join(" ", new[] { artist, title }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        QueryBox.Text = query;

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(query))
                _ = RunSearch(query);
            else
                QueryBox.Focus();
        };
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = RunSearch(QueryBox.Text);
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = RunSearch(QueryBox.Text);

    // Останавливает текущий поиск: отменяет токен (обрывает и основной запрос списка, и уже
    // запущенные загрузки миниатюр), возвращает интерфейс в состояние "готов к новому поиску".
    // RunSearch сам аккуратно завершается по OperationCanceledException — здесь только UI.
    private void CancelSearchButton_Click(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();

        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = LocalizationService.Translate("Поиск отменён");
        ResultsScrollViewer.Visibility = Visibility.Collapsed;

        SearchButton.IsEnabled = true;
        CancelSearchButton.Visibility = Visibility.Collapsed;
    }

    private async Task RunSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;

        // Новый поиск отменяет предыдущий, если тот ещё не завершился — иначе миниатюры
        // от старого запроса могли бы дорисоваться поверх результатов нового.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        ResultsPanel.Children.Clear();
        ResultsScrollViewer.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = LocalizationService.Translate("Ищем…");
        SearchButton.IsEnabled = false;
        CancelSearchButton.Visibility = Visibility.Visible;

        try
        {
            // Оба источника запрашиваются параллельно и независимо друг от друга: если один
            // упал с ошибкой (сеть, таймаут, блокировка) — SearchItunesAsync/SearchDeezerAsync
            // сами гасят исключение и возвращают пустой список, чтобы не обрушить второй.
            var itunesTask = SearchItunesAsync(query, token);
            var deezerTask = SearchDeezerAsync(query, token);
            await Task.WhenAll(itunesTask, deezerTask);

            token.ThrowIfCancellationRequested();

            var entries = MergeAndDedupe(itunesTask.Result, deezerTask.Result);

            if (entries.Count == 0)
            {
                StatusText.Text = LocalizationService.Translate("Ничего не найдено. Попробуйте изменить запрос.");
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Visible;

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                await AddResultTile(entry, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Отменено явно кнопкой "Отмена" (или перекрыто новым поиском) — CancelSearchButton_Click
            // уже сам поставил подходящий статус-текст, здесь ничего дополнительно делать не надо.
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            StatusText.Visibility = Visibility.Visible;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
            StatusText.Text = LocalizationService.Translate($"Не удалось выполнить поиск: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                SearchButton.IsEnabled = true;
                CancelSearchButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ---------- iTunes Search API ----------

    private static async Task<List<ArtResult>> SearchItunesAsync(string query, CancellationToken token)
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
            // Настоящая отмена — либо пользователь нажал "Отмена", либо запущен новый поиск
            // поверх этого (см. _searchCts?.Cancel() в начале RunSearch). Пробрасываем дальше,
            // чтобы RunSearch мог сам корректно завершиться через ThrowIfCancellationRequested.
            throw;
        }
        catch
        {
            // Сюда же попадает и TaskCanceledException от СОБСТВЕННОГО таймаута HttpClient
            // (Http.Timeout = 15 секунд, см. поле выше) — она тоже наследуется от
            // OperationCanceledException, но НЕ связана с нашим token: если ловить её как
            // обычную отмену (как было раньше), исключение улетало бы вверх до RunSearch,
            // который принял бы его за настоящую отмену пользователем и не обновил бы
            // интерфейс вообще — экран так и оставался на "Ищем…" навсегда, хотя запрос давно
            // не выполняется. Сеть недоступна, iTunes вернул ошибку, JSON не распарсился,
            // истёк таймаут и т.п. — во всех этих случаях второй источник (Deezer) всё ещё
            // может найти результат, поэтому просто отдаём пустой список вместо того, чтобы
            // обрушить весь поиск целиком.
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

            entries.Add(new ArtResult(WithItunesArtworkSize(artwork, 200), WithItunesArtworkSize(artwork, 1200), label));
        }

        return entries;
    }

    // Ссылки iTunes на обложки содержат размер прямо в пути (например ".../100x100bb.jpg") —
    // подставляя своё значение вместо 100, можно получить то же изображение в нужном разрешении.
    private static string WithItunesArtworkSize(string artworkUrl, int size) =>
        Regex.Replace(artworkUrl, @"\d+x\d+bb(?=\.\w+$)", $"{size}x{size}bb");

    // ---------- Deezer Search API ----------

    private static async Task<List<ArtResult>> SearchDeezerAsync(string query, CancellationToken token)
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

            entries.Add(new ArtResult(thumb, full, label));
        }

        return entries;
    }

    // ---------- Объединение результатов из обоих источников ----------

    // Простое чередование (по одному из каждого источника) вместо "сначала все iTunes, потом
    // все Deezer" — так пользователь сразу видит, что источников несколько и они разные,
    // а не долистывает вниз в поисках второго. Дубликаты между источниками не схлопываются
    // (адреса обложек у них никогда не совпадают буквально), но это не страшно — совсем
    // одинаковых на вид миниатюр из разных источников почти не бывает.
    private static List<ArtResult> MergeAndDedupe(List<ArtResult> itunes, List<ArtResult> deezer)
    {
        var merged = new List<ArtResult>(itunes.Count + deezer.Count);
        int max = Math.Max(itunes.Count, deezer.Count);
        for (int i = 0; i < max; i++)
        {
            if (i < itunes.Count) merged.Add(itunes[i]);
            if (i < deezer.Count) merged.Add(deezer[i]);
        }
        return merged;
    }

    // ---------- Отображение результатов ----------

    private async Task AddResultTile(ArtResult entry, CancellationToken token)
    {
        byte[] thumbBytes;
        try
        {
            thumbBytes = await GetImageBytesAsync(entry.ThumbUrl, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return; // пропускаем результат, у которого не загрузилась миниатюра (включая таймаут)
        }

        BitmapImage thumb;
        try
        {
            thumb = BytesToBitmap(thumbBytes);
        }
        catch
        {
            return;
        }

        var image = new System.Windows.Controls.Image
        {
            Source = thumb,
            Width = 96,
            Height = 96,
            Stretch = Stretch.UniformToFill
        };

        var imageHost = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = (Brush)FindResource("ControlFillColorSecondaryBrush"),
            Child = image
        };

        var caption = new System.Windows.Controls.TextBlock
        {
            Text = entry.Label,
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 96,
            MaxHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var tile = new StackPanel
        {
            Width = 96,
            Margin = new Thickness(6),
            Cursor = Cursors.Hand
        };
        tile.Children.Add(imageHost);
        tile.Children.Add(caption);
        tile.MouseLeftButtonDown += async (_, _) => await SelectResult(entry.FullUrl);

        ResultsPanel.Children.Add(tile);
    }

    private async Task SelectResult(string fullUrl)
    {
        ResultsPanel.IsEnabled = false;
        try
        {
            var bytes = await GetImageBytesAsync(fullUrl, CancellationToken.None);

            SelectedImageBytes = bytes;
            SelectedImageMimeType = "image/jpeg"; // оба источника отдают JPEG для таких URL
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось загрузить обложку:\n{ex.Message}",
                "Ошибка загрузки", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            ResultsPanel.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        base.OnClosed(e);
    }

    private static async Task<byte[]> GetImageBytesAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !TrustedImageHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                                           uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Источник изображения не входит в список доверенных HTTPS-доменов.");

        string cachePath = GetArtworkCachePath(uri);
        if (await TryReadCachedArtworkAsync(cachePath, token) is { } cachedBytes)
            return cachedBytes;

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBytesWithLimitAsync(response.Content, MaxImageBytes, token);
        if (!IsDecodableArtwork(bytes))
            throw new InvalidDataException("Сервер вернул данные, которые не являются поддерживаемым изображением.");

        await WriteArtworkCacheAsync(cachePath, bytes, token);
        _ = Task.Run(TrimArtworkCache);
        return bytes;
    }

    private static string GetArtworkCachePath(Uri uri)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
        return Path.Combine(ArtworkCacheDirectory, Convert.ToHexString(hash) + ".img");
    }

    private static async Task<byte[]?> TryReadCachedArtworkAsync(string cachePath, CancellationToken token)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;

            var info = new FileInfo(cachePath);
            if (info.Length <= 0 || info.Length > MaxImageBytes)
            {
                TryDeleteCacheFile(cachePath);
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(cachePath, token);
            if (!IsDecodableArtwork(bytes))
            {
                TryDeleteCacheFile(cachePath);
                return null;
            }

            // LastAccessTime может быть выключен политикой Windows, поэтому обновляем
            // LastWriteTime сами и используем его как переносимый LRU-признак при очистке.
            // Даже если дата недоступна (например, read-only каталог), корректные байты
            // остаются полезным попаданием кэша и не должны вызывать новую загрузку.
            try { File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow); }
            catch { /* Используем файл без обновления LRU-метки. */ }
            return bytes;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Проблема только с кэшем не должна мешать поиску: при недоступном файле просто
            // используем уже защищённую сетевую загрузку ниже.
            return null;
        }
    }

    private static async Task WriteArtworkCacheAsync(string cachePath, byte[] bytes, CancellationToken token)
    {
        string temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(ArtworkCacheDirectory);
            await File.WriteAllBytesAsync(temporaryPath, bytes, token);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Кэш — ускорение, а не обязательная часть выбора обложки. Ошибка диска не должна
            // отменять уже успешно полученное из доверенного источника изображение.
        }
        finally
        {
            TryDeleteCacheFile(temporaryPath);
        }
    }

    private static bool IsDecodableArtwork(byte[] bytes)
    {
        try
        {
            _ = BytesToBitmap(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrimArtworkCache()
    {
        try
        {
            if (!Directory.Exists(ArtworkCacheDirectory)) return;

            var files = Directory.EnumerateFiles(ArtworkCacheDirectory, "*.img")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            long totalBytes = files.Sum(file => file.Length);
            int remainingFiles = files.Count;
            foreach (var file in files)
            {
                if (remainingFiles <= ArtworkCacheMaxFiles && totalBytes <= ArtworkCacheMaxBytes) break;

                try
                {
                    file.Delete();
                    totalBytes -= file.Length;
                    remainingFiles--;
                }
                catch
                {
                    // Один занятый/защищённый файл не должен останавливать очистку остальных.
                }
            }
        }
        catch
        {
            // Очистка выполняется в фоне и не влияет ни на выбор обложки, ни на UI.
        }
    }

    // Вызывается только по явному действию пользователя из SettingsWindow. Удаляем файлы
    // непосредственно в известной папке кэша, не используем путь или маску, пришедшие из UI,
    // и не трогаем вложенные каталоги. Параллельная загрузка может создать новую запись уже
    // после очистки — это нормальное и безопасное поведение.
    public static ArtworkCacheClearResult ClearArtworkCache()
    {
        int deletedFiles = 0;
        long freedBytes = 0;
        int failedFiles = 0;

        try
        {
            if (!Directory.Exists(ArtworkCacheDirectory))
                return new ArtworkCacheClearResult(0, 0, 0);

            foreach (var path in Directory.EnumerateFiles(ArtworkCacheDirectory).ToList())
            {
                try
                {
                    long length = new FileInfo(path).Length;
                    File.Delete(path);
                    deletedFiles++;
                    freedBytes += length;
                }
                catch
                {
                    failedFiles++;
                }
            }

            // Удаляем пустую папку, но не считаем это ошибкой: она может снова создаваться
            // параллельной загрузкой в тот же момент.
            try { Directory.Delete(ArtworkCacheDirectory, recursive: false); }
            catch { /* В каталоге остались занятые файлы либо он уже создан заново. */ }
        }
        catch
        {
            // Невозможность перечислить каталог не должна приводить к падению окна настроек.
            failedFiles++;
        }

        return new ArtworkCacheClearResult(deletedFiles, freedBytes, failedFiles);
    }

    private static void TryDeleteCacheFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Невозможность удалить устаревший кэш безопасна: следующая запись всё равно
            // использует отдельный временный файл и атомарную замену.
        }
    }

    private static async Task<byte[]> ReadBytesWithLimitAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw new InvalidDataException("Ответ превышает допустимый размер.");

        await using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > maxBytes)
                throw new InvalidDataException("Ответ превышает допустимый размер.");
            await memory.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return memory.ToArray();
    }

    private static BitmapImage BytesToBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
