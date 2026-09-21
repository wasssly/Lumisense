using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using ArtResult = Lumisense.CoverArtProviders.ArtResult;

namespace Lumisense;

// Поиск по трём открытым API без ключа (см. CoverArtProviders). Genius не подключен: его API
// отдаёт обложки только вместе с текстами и требует личный Client Access Token.
public partial class CoverArtSearchWindow : FluentWindow
{
    private const int MaxImageBytes = 10 * 1024 * 1024;

    // SHA-256 URL в имя файла — короткий детерминированный ключ без path traversal. Лимиты
    // ниже не дают кэшу занимать место на диске бесконтрольно.
    private const int ArtworkCacheMaxFiles = 256;
    private const long ArtworkCacheMaxBytes = 128L * 1024 * 1024;
    private static readonly string ArtworkCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense", "cover-cache");

    // Результат ручной очистки для интерфейса настроек. FailedFiles > 0 обычно означает,
    // что параллельно с очисткой какой-то файл кэша был занят новой загрузкой.
    public readonly record struct ArtworkCacheClearResult(int DeletedFiles, long FreedBytes, int FailedFiles);

    // Заполняется только если пользователь кликнул по одному из найденных вариантов —
    // при закрытии окна без выбора (Escape/крестик/"Закрыть") остаётся null.
    public byte[]? SelectedImageBytes { get; private set; }
    public string? SelectedImageMimeType { get; private set; }

    // Отменяет предыдущий незавершённый поиск (и его запросы миниатюр) при новом запуске или
    // явной "Отмене" — иначе смена запроса оставляла бы гоняться по сети старые запросы.
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

    // Отменяет токен (запрос и загрузки миниатюр) и возвращает UI в состояние "готов к поиску".
    // RunSearch завершается сам по OperationCanceledException.
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
            // Источники запрашиваются параллельно и независимо: упавший с ошибкой (сеть,
            // таймаут) возвращает пустой список вместо обрушения остальных.
            var searchTasks = new List<Task<List<ArtResult>>>();
            if (ItunesSourceCheckBox.IsChecked == true) searchTasks.Add(CoverArtProviders.SearchItunesAsync(query, token));
            if (DeezerSourceCheckBox.IsChecked == true) searchTasks.Add(CoverArtProviders.SearchDeezerAsync(query, token));
            if (MusicBrainzSourceCheckBox.IsChecked == true) searchTasks.Add(CoverArtProviders.SearchMusicBrainzAsync(query, token));

            if (searchTasks.Count == 0)
            {
                StatusText.Text = LocalizationService.Translate("Выберите хотя бы один источник обложек.");
                return;
            }

            await Task.WhenAll(searchTasks);
            token.ThrowIfCancellationRequested();

            var entries = MergeAndDedupe(searchTasks.Select(t => t.Result).ToList());

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
            // Отменено кнопкой "Отмена" или перекрыто новым поиском — статус-текст уже
            // поставлен в CancelSearchButton_Click.
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

    // Чередование по одному результату из каждого источника, чтобы сразу было видно несколько источников;
    // дубликаты не схлопываются — адреса обложек у источников не совпадают буквально.
    private static List<ArtResult> MergeAndDedupe(List<List<ArtResult>> sources)
    {
        int total = sources.Sum(s => s.Count);
        var merged = new List<ArtResult>(total);
        int max = sources.Count == 0 ? 0 : sources.Max(s => s.Count);
        for (int i = 0; i < max; i++)
        {
            foreach (var source in sources)
            {
                if (i < source.Count) merged.Add(source[i]);
            }
        }
        return merged;
    }

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
            !CoverArtProviders.TrustedImageHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                                           uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Источник изображения не входит в список доверенных HTTPS-доменов.");

        string cachePath = GetArtworkCachePath(uri);
        if (await TryReadCachedArtworkAsync(cachePath, token) is { } cachedBytes)
            return cachedBytes;

        using var response = await CoverArtProviders.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var bytes = await CoverArtProviders.ReadBytesWithLimitAsync(response.Content, MaxImageBytes, token);
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

            // LastAccessTime может быть выключен политикой Windows — обновляем LastWriteTime
            // сами и используем как переносимый LRU-признак.
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

    // Вызывается только по явному действию пользователя из SettingsWindow. Удаляет файлы
    // напрямую в известной папке кэша, не по пути/маске из UI, без вложенных каталогов.
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
