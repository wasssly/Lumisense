using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WpfBorder = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using WpfImage = System.Windows.Controls.Image;
using WpfPanel = System.Windows.Controls.Panel;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Стиль смены обложки "Carousel": на время перехода видны предыдущая, текущая и следующая обложки, лента едет на один шаг.
public partial class MainWindow
{
    private const double CarouselSpacing = 112;
    private const double CarouselCoverSize = 150;
    private const int NeighborArtReach = 2;
    private const int NeighborArtDecodeWidth = 256;
    private const int NeighborArtCacheLimit = 16;

    // Путь трека, чья обложка показана сейчас: нужен, чтобы при смене найти соседей и уходящего, и нового трека.
    private string? _lastArtPath;
    private readonly Dictionary<string, ImageSource?> _neighborArtCache = new(StringComparer.OrdinalIgnoreCase);
    private int _neighborArtGeneration;

    private bool _carouselAnimating;

    // Карусель включена: по бокам главной обложки всегда видны соседние (приглушённые), а смена трека двигает ленту.
    private bool IsCarouselTransition => _settings.AlbumArtTransitionEnabled && _settings.AlbumArtTransitionStyle == "Carousel";

    // Соседи по порядку плейлиста, а в шаффле — по уже известной истории (будущий трек, которого в истории ещё нет, заранее
    // неизвестен, и вместо него показывается заглушка).
    private string? GetNeighborTrackPath(string? path, int offset)
    {
        if (path is null) return null;
        if (_shuffleSession.IsEnabled) return _shuffleSession.PeekNeighbor(path, offset);

        var tracks = FlattenActive();
        int index = tracks.IndexOf(path);
        if (index < 0 || tracks.Count < 2) return null;

        int neighbor = ((index + offset) % tracks.Count + tracks.Count) % tracks.Count;
        return neighbor == index ? null : tracks[neighbor];
    }

    // Вызывается из настроек при выборе стиля: к первой смене обложки соседи уже должны быть в кэше.
    public void ApplyAlbumArtTransitionStyleLive()
    {
        PrefetchNeighborArt(_currentTrackPath);
        RefreshCarouselSides();
    }

    // Статичные боковые карточки вокруг главной обложки; при выключенной карусели лента пуста.
    private void RefreshCarouselSides()
    {
        if (_carouselAnimating) return;

        ClearCarouselLayers();
        if (!IsCarouselTransition) return;

        foreach (int side in new[] { -1, 1 })
        {
            string? neighborPath = GetNeighborTrackPath(_currentTrackPath, side);
            if (neighborPath is null) continue;

            var card = CreateCarouselCard(GetCachedNeighborArt(neighborPath), out WpfBorder dim);
            card.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(SlotScale(side), SlotScale(side)), new TranslateTransform(side * CarouselSpacing, 0) }
            };
            dim.Opacity = SlotDim(side);
            AlbumArtCarouselHost.Children.Add(card);
        }

        AlbumArtCarouselHost.Visibility = Visibility.Visible;
    }

    private void PrefetchNeighborArt(string? centerPath)
    {
        if (!IsCarouselTransition) return;

        var wanted = new List<string>();
        for (int offset = -NeighborArtReach; offset <= NeighborArtReach; offset++)
        {
            if (offset != 0 && GetNeighborTrackPath(centerPath, offset) is { } neighbor && !wanted.Contains(neighbor))
                wanted.Add(neighbor);
        }

        if (_neighborArtCache.Count > NeighborArtCacheLimit)
        {
            foreach (string stale in _neighborArtCache.Keys.Where(key => !wanted.Contains(key)).ToList())
                _neighborArtCache.Remove(stale);
        }

        var missing = wanted.Where(path => !_neighborArtCache.ContainsKey(path)).ToList();
        if (missing.Count == 0) return;

        FireAndForget(LoadNeighborArtAsync(missing, ++_neighborArtGeneration), "LoadNeighborArtAsync");
    }

    private async Task LoadNeighborArtAsync(List<string> paths, int generation)
    {
        foreach (string path in paths)
        {
            // Трек успел смениться: оставшиеся обложки уже не нужны, новый запрос загрузит свои.
            if (generation != _neighborArtGeneration || _isExiting) return;
            _neighborArtCache[path] = await Task.Run(() => ReadNeighborArt(path));
            RefreshCarouselSides();
        }
    }

    private static ImageSource? ReadNeighborArt(string path)
    {
        try
        {
            var pictures = new ATL.Track(path).EmbeddedPictures;
            if (pictures.Count == 0) return null;

            using var stream = new MemoryStream(pictures[0].PictureData);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = NeighborArtDecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // Нет тегов или битая картинка: соседняя карточка просто остаётся заглушкой.
            return null;
        }
    }

    private ImageSource? GetCachedNeighborArt(string? path) =>
        path is not null && _neighborArtCache.TryGetValue(path, out var art) ? art : null;

    // d = +1 для Next (лента едет влево), -1 для Previous. Слоты: 0 — центр, ±1 — боковые, ±2 — уходят за край.
    private void RunCarouselTransition(AlbumArtTransitionDirection direction, ImageSource? oldArt, ImageSource? newArt,
        string? oldPath, string? newPath, TimeSpan duration, int transitionGeneration)
    {
        int d = direction == AlbumArtTransitionDirection.Next ? 1 : -1;
        var slots = new (ImageSource? Art, int From, int To, int Z)[]
        {
            (GetCachedNeighborArt(GetNeighborTrackPath(oldPath, -d)), -d, -2 * d, 1),
            (oldArt, 0, -d, 2),
            (newArt, d, 0, 4),
            (GetCachedNeighborArt(GetNeighborTrackPath(newPath, d)), 2 * d, d, 3),
        };

        _carouselAnimating = true;
        AlbumArtCarouselHost.Children.Clear();
        AlbumArtCarouselHost.Visibility = Visibility.Visible;
        // Основная обложка уже получила новое изображение, но на время ленты скрыта: вместо неё едет карточка ленты.
        AlbumArtBorder.Opacity = 0;

        // Обложки запоминаем: при следующем шаге они станут боковыми и их не придётся читать из тегов.
        if (oldPath is not null && oldArt is not null) _neighborArtCache[oldPath] = oldArt;
        if (newPath is not null && newArt is not null) _neighborArtCache[newPath] = newArt;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        foreach (var slot in slots)
        {
            var scale = new ScaleTransform(SlotScale(slot.From), SlotScale(slot.From));
            var translate = new TranslateTransform(slot.From * CarouselSpacing, 0);
            var card = CreateCarouselCard(slot.Art, out WpfBorder dim);
            card.RenderTransform = new TransformGroup { Children = { scale, translate } };
            card.Opacity = SlotOpacity(slot.From);
            dim.Opacity = SlotDim(slot.From);
            WpfPanel.SetZIndex(card, slot.Z);
            AlbumArtCarouselHost.Children.Add(card);

            var slide = new DoubleAnimation(slot.From * CarouselSpacing, slot.To * CarouselSpacing, duration) { EasingFunction = ease };
            if (slot.To == 0)
            {
                slide.Completed += (_, _) =>
                {
                    if (transitionGeneration != _albumArtTransitionGeneration) return;
                    _carouselAnimating = false;
                    RefreshCarouselSides();
                    AlbumArtBorder.Opacity = 1;
                };
            }
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
            var scaleAnimation = new DoubleAnimation(SlotScale(slot.From), SlotScale(slot.To), duration) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(SlotOpacity(slot.From), SlotOpacity(slot.To), duration) { EasingFunction = ease });
            dim.BeginAnimation(OpacityProperty,
                new DoubleAnimation(SlotDim(slot.From), SlotDim(slot.To), duration) { EasingFunction = ease });
        }
    }

    private void ClearCarouselLayers()
    {
        // Сброс слоёв (в том числе перед новой сменой) обрывает анимацию ленты, поэтому её флаг тоже снимается.
        _carouselAnimating = false;
        AlbumArtCarouselHost.Children.Clear();
        AlbumArtCarouselHost.Visibility = Visibility.Collapsed;
    }

    private static double SlotScale(int slot) => Math.Abs(slot) switch { 0 => 1.0, 1 => 0.74, _ => 0.58 };

    // Боковые обложки видны, но приглушены тёмным слоем; дальше крайних они плавно исчезают.
    private static double SlotOpacity(int slot) => Math.Abs(slot) <= 1 ? 1.0 : 0.0;

    private static double SlotDim(int slot) => Math.Abs(slot) switch { 0 => 0.0, 1 => 0.55, _ => 0.8 };

    private static Grid CreateCarouselCard(ImageSource? art, out WpfBorder dim)
    {
        var card = new Grid
        {
            Width = CarouselCoverSize,
            Height = CarouselCoverSize,
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false,
        };

        if (art is null)
        {
            var placeholder = new WpfBorder { CornerRadius = new CornerRadius(16) };
            placeholder.SetResourceReference(WpfBorder.BackgroundProperty, "ControlFillColorSecondaryBrush");
            var icon = new SvgPathIcon { Icon = "IconMusicNote", Size = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            card.Children.Add(placeholder);
            card.Children.Add(icon);
        }
        else
        {
            var image = new WpfImage
            {
                Source = art,
                Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, CarouselCoverSize, CarouselCoverSize), 16, 16),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            card.Children.Add(image);
        }

        dim = new WpfBorder { CornerRadius = new CornerRadius(16), Background = Brushes.Black, IsHitTestVisible = false };
        card.Children.Add(dim);
        return card;
    }
}
