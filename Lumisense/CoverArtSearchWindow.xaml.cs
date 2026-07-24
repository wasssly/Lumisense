using System.IO;
using System.Linq;
using System.Net.Http;
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

namespace AudioPlayer;

/// <summary>
/// Поиск обложки трека в интернете по исполнителю и названию — через два открытых поисковых
/// API, не требующих ключа или регистрации: iTunes Search (search.itunes.apple.com) и Deezer
/// (api.deezer.com). Оба запроса летят параллельно, а результаты объединяются в один список.
///
/// Источник только один (iTunes) раньше нередко не находил обложки исполнителей вне основного
/// каталога американского iTunes Store — в первую очередь русских и вообще СНГ-исполнителей,
/// но не только их: то же самое бывает и с локальными/независимыми артистами из других стран,
/// которых просто нет в конкретном региональном каталоге Apple. У Deezer каталог во многом
/// пересекается, но не идентичен, поэтому вместе оба источника закрывают ощутимо больше
/// запросов, чем любой из них по отдельности. Если один источник не отвечает или падает с
/// ошибкой (например, заблокирован в сети пользователя) — это не мешает второму всё равно
/// показать свои результаты.
///
/// Показывает найденные варианты миниатюрами; при выборе скачивает изображение в повышенном
/// разрешении и возвращает его вызывающей стороне (TrackTagsWindow) — та сохраняет его точно
/// так же, как обложку, выбранную с диска.
///
/// Полноценный поиск конкретно через Genius не подключен: их API отдаёт обложки только
/// вместе с текстами песен и требует личный Client Access Token (регистрация приложения на
/// genius.com/api-clients) — то есть без ключа, вписанного пользователем в настройки, он в
/// принципе не заработает. iTunes и Deezer выбраны как источники, которые работают "из коробки".
/// </summary>
public partial class CoverArtSearchWindow : FluentWindow
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

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

    public CoverArtSearchWindow(string? artist, string? title)
    {
        InitializeComponent();

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
        StatusText.Text = "Поиск отменён";
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
        StatusText.Text = "Ищем…";
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
                StatusText.Text = "Ничего не найдено. Попробуйте изменить запрос.";
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
            StatusText.Text = $"Не удалось выполнить поиск: {ex.Message}";
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
            var json = await response.Content.ReadAsStringAsync(token);
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
            var json = await response.Content.ReadAsStringAsync(token);
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
            thumbBytes = await Http.GetByteArrayAsync(entry.ThumbUrl, token);
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
            var bytes = await Http.GetByteArrayAsync(fullUrl);

            SelectedImageBytes = bytes;
            SelectedImageMimeType = "image/jpeg"; // оба источника отдают JPEG для таких URL
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось загрузить обложку:\n{ex.Message}",
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
