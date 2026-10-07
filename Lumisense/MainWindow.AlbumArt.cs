using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using WpfSlider = System.Windows.Controls.Slider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lumisense;

// Обложка альбома: жесты, меню, загрузка и анимация смены. Вынесено из MainWindow.xaml.cs
// только ради навигации, логика не менялась.
public partial class MainWindow
{
    // Направление анимации смены обложки (AnimateAlbumArtTransition): Next — старая улетает влево, новая приходит справа,
    // Previous — наоборот, None — без анимации (например, первая загрузка при старте).
    private enum AlbumArtTransitionDirection { None, Next, Previous }

    // Смена обложки может происходить, пока WPF ещё распространяет DynamicResource по Popup
    // и анимируемому дереву. Объединяем обновления темы и выполняем их после текущего UI-прохода.
    private bool _albumArtAppearanceRefreshPending;
    private readonly AlbumArtTransitionBurstPolicy _albumArtTransitionBurstPolicy =
        new(TimeSpan.FromMilliseconds(240));
    private int _albumArtTransitionGeneration;

    // Жесты на обложке: короткое касание управляет паузой, а сдвиг не меньше 28 DIP
    // распознаётся как смена трека (горизонталь) или изменение громкости (вертикаль).
    private Point _albumArtGestureStart;
    private bool _isAlbumArtGestureActive;
    private bool _albumArtGestureMoved;
    private const double AlbumArtGestureThreshold = 28.0;

    // Оптимизированная UI-копия обложки (или null): ограничена по ширине при декодировании, чтобы HighQuality-отрисовка и
    // анимация оставались плавными на многомегапиксельных covers; полный размер лениво читается из _currentAlbumArtBytes.
    private BitmapImage? _currentAlbumArt;
    private ImageBrush? _currentArtBrush;

    // Исходные байты и MIME-тип обложки из тега — для контекстного меню: "Скачать изображение" пишет эти байты как есть,
    // а "Свойства" показывает реальные формат и размер файла.
    private byte[]? _currentAlbumArtBytes;
    private string? _currentAlbumArtMimeType;
    private AlbumArtPictureKind? _currentAlbumArtPictureType;

    private void AlbumArtBorder_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_settings.AlbumArtGesturesEnabled)
        {
            OpenAlbumArtPreview();
            e.Handled = true;
            return;
        }

        _albumArtGestureStart = e.GetPosition(AlbumArtBorder);
        _albumArtGestureMoved = false;
        _isAlbumArtGestureActive = AlbumArtBorder.CaptureMouse();
    }

    private void AlbumArtBorder_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isAlbumArtGestureActive || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

        Vector delta = e.GetPosition(AlbumArtBorder) - _albumArtGestureStart;
        if (delta.Length >= AlbumArtGestureThreshold)
            _albumArtGestureMoved = true;
    }

    private void AlbumArtBorder_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isAlbumArtGestureActive) return;

        Point end = e.GetPosition(AlbumArtBorder);
        AlbumArtBorder.ReleaseMouseCapture();
        _isAlbumArtGestureActive = false;

        Vector delta = end - _albumArtGestureStart;
        if (!_albumArtGestureMoved || delta.Length < AlbumArtGestureThreshold)
        {
            ExternalPlayPause();
            e.Handled = true;
            return;
        }

        if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
        {
            if (delta.X < 0) ExternalNext();
            else ExternalPrev();
        }
        else
        {
            ExternalChangeVolume(delta.Y < 0 ? 0.04 : -0.04);
        }

        e.Handled = true;
    }

    // Просмотр обложки остаётся доступен из контекстного меню. Так короткий клик на самой
    // обложке можно использовать как предсказуемый жест пуск/пауза, не теряя эту функцию.
    private void OpenAlbumArtPreview()
    {
        // У трека может не быть обложки (показан плейсхолдер-иконка) — тогда открывать нечего
        BitmapImage? previewArt = DecodeOriginalAlbumArt() ?? _currentAlbumArt;
        if (previewArt is null) return;

        if (_coverArtWindow == null)
        {
            _coverArtWindow = new CoverArtWindow(previewArt, TrackTitleText.Text, _settings)
            {
                Owner = this
            };

            // Screen.WorkingArea в физических пикселях, а Left/Top/Width/Height окна — в DIP: прямое присваивание давало окно больше
            // рабочей области при масштабе 125/150/200%, поэтому пересчитываем через DPI главного окна.
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var workArea = screen.WorkingArea;
            var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                             ?? Matrix.Identity;
            var workTopLeft = fromDevice.Transform(new Point(workArea.Left, workArea.Top));

            _coverArtWindow.WindowState = WindowState.Normal;
            _coverArtWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _coverArtWindow.Left = workTopLeft.X;
            _coverArtWindow.Top = workTopLeft.Y;
            _coverArtWindow.Width = Math.Max(_coverArtWindow.MinWidth, workArea.Width * fromDevice.M11);
            _coverArtWindow.Height = Math.Max(_coverArtWindow.MinHeight, workArea.Height * fromDevice.M22);

            _coverArtWindow.Closed += (_, _) => _coverArtWindow = null;
            _coverArtWindow.Show();
        }
        else
        {
            _coverArtWindow.Activate();
        }
    }

    private void OpenAlbumArtMenuItem_Click(object sender, RoutedEventArgs e) => OpenAlbumArtPreview();

    // Обложки может не быть (плейсхолдер-иконка) — тогда контекстное меню показывать не о
    // чем, все четыре пункта всё равно ничего бы не сделали.
    private void AlbumArtBorder_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (_currentAlbumArt is null) e.Handled = true;
    }

    // Полный размер нужен редко — только по явной команде пользователя. Декодируем его из
    // исходных bytes с OnLoad, чтобы поток безопасно закрыть до показа отдельного окна.
    private BitmapImage? DecodeOriginalAlbumArt()
    {
        if (_currentAlbumArtBytes is null) return null;

        try
        {
            using var stream = new MemoryStream(_currentAlbumArtBytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось декодировать полноразмерную обложку: {ex.Message}");
            return null;
        }
    }

    private static string MimeTypeToExtension(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/bmp" => ".bmp",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    private static string MimeTypeToFilter(string extension) => extension switch
    {
        ".png" => "Изображение PNG (*.png)|*.png",
        ".bmp" => "Изображение BMP (*.bmp)|*.bmp",
        ".gif" => "Изображение GIF (*.gif)|*.gif",
        ".webp" => "Изображение WebP (*.webp)|*.webp",
        _ => "Изображение JPEG (*.jpg)|*.jpg"
    };

    // Имя файла по умолчанию в диалоге сохранения — название трека (если есть), иначе
    // просто "Обложка", с заменой символов, недопустимых в имени файла Windows.
    //
    // Раньше сравнивали TrackTitleText.Text с русским плейсхолдером "Файл не выбран" — на английском
    // интерфейсе там "No file selected", и baseName ошибочно становился этим текстом. Проверяем сам
    // факт загруженного трека (_currentTrackPath), а не текст в UI.
    private string SuggestAlbumArtFileName()
    {
        string baseName = _currentTrackPath != null && !string.IsNullOrWhiteSpace(TrackTitleText.Text)
            ? TrackTitleText.Text
            : LocalizationService.Translate("Обложка");

        foreach (char c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');

        return baseName;
    }

    private void DownloadAlbumArtMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAlbumArtBytes is null) return;

        string extension = MimeTypeToExtension(_currentAlbumArtMimeType);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить обложку",
            FileName = SuggestAlbumArtFileName() + extension,
            Filter = MimeTypeToFilter(extension)
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _currentAlbumArtBytes);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось сохранить изображение:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void CopyAlbumArtMenuItem_Click(object sender, RoutedEventArgs e)
    {
        BitmapImage? copyArt = DecodeOriginalAlbumArt() ?? _currentAlbumArt;
        if (copyArt is null) return;

        try
        {
            System.Windows.Clipboard.SetImage(copyArt);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось скопировать изображение:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void AlbumArtPropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAlbumArtBytes is null) return;
        BitmapImage? propertiesArt = DecodeOriginalAlbumArt() ?? _currentAlbumArt;
        if (propertiesArt is null) return;

        var propsWindow = new CoverArtPropertiesWindow(
            propertiesArt, _currentAlbumArtBytes, _currentAlbumArtMimeType, _currentAlbumArtPictureType,
            TrackTitleText.Text, TrackArtistText.Text, _currentTrackPath, _settings)
        {
            Owner = this
        };
        propsWindow.ShowDialog();
    }

    private void ApplyPreparedAlbumArt(PreparedTrack loaded, AlbumArtTransitionDirection direction)
    {
        if (loaded.AlbumArt is not null)
        {
            ApplyAlbumArtImage(loaded.AlbumArt, direction);
            _currentAlbumArt = loaded.AlbumArt;
            _currentAlbumArtBytes = loaded.AlbumArtBytes;
            _currentAlbumArtMimeType = loaded.AlbumArtMimeType;
            _currentAlbumArtPictureType = loaded.AlbumArtPictureType;
        }
        else
        {
            ResetAlbumArtPlaceholder(direction);
        }

        QueueAlbumArtAppearanceRefresh();
    }

    private void LoadAlbumArt(string filePath, AlbumArtTransitionDirection direction = AlbumArtTransitionDirection.None)
    {
        try
        {
            var tagFile = new ATL.Track(filePath);
            var pictures = tagFile.EmbeddedPictures;

            if (pictures.Count > 0)
            {
                var bytes = pictures[0].PictureData;
                using var ms = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = ArtworkDisplayDecodePixelWidth;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                ApplyAlbumArtImage(bitmap, direction);
                _currentAlbumArt = bitmap;
                _currentAlbumArtBytes = bytes;
                _currentAlbumArtMimeType = AlbumArtPictureKindExtensions.DetectAlbumArtMimeType(bytes);
                _currentAlbumArtPictureType = AlbumArtPictureKindExtensions.FromAtl(pictures[0].PicType);
            }
            else
            {
                ResetAlbumArtPlaceholder(direction);
            }

            // Если в тегах есть название и исполнитель — покажем их вместо имени файла/папки
            if (!string.IsNullOrWhiteSpace(tagFile.Title) || !string.IsNullOrWhiteSpace(tagFile.Artist))
            {
                SetTrackInfoText(
                    !string.IsNullOrWhiteSpace(tagFile.Title) ? tagFile.Title : TrackTitleText.Text,
                    !string.IsNullOrWhiteSpace(tagFile.Artist) ? tagFile.Artist : TrackArtistText.Text);
            }
        }
        catch
        {
            // Файл без тегов, повреждённые метаданные и т.п. — просто показываем плейсхолдер
            ResetAlbumArtPlaceholder(direction);
        }

        QueueAlbumArtAppearanceRefresh();
    }

    private void QueueAlbumArtAppearanceRefresh()
    {
        if (_settings.AccentColorMode != "Cover" && !_settings.CoverBaseFromCover)
            return;

        if (_albumArtAppearanceRefreshPending || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _albumArtAppearanceRefreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _albumArtAppearanceRefreshPending = false;
            if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            if (_settings.AccentColorMode == "Cover")
                ApplyAccentColor();
            else
                ApplyCoverBaseBackground();
        }), DispatcherPriority.ContextIdle);
    }

    private void ApplyAlbumArtImage(ImageSource imageSource, AlbumArtTransitionDirection direction = AlbumArtTransitionDirection.None)
    {
        AnimateAlbumArtTransition(direction, () =>
        {
            AlbumArtImage.Source = imageSource;
            AlbumArtImage.Visibility = Visibility.Visible;
            AlbumArtBorder.Background = Brushes.Transparent;
            AlbumArtIcon.Visibility = Visibility.Collapsed;

            var sharedBrush = new ImageBrush(imageSource) { Stretch = Stretch.UniformToFill };
            if (sharedBrush.CanFreeze) sharedBrush.Freeze();
            _currentArtBrush = sharedBrush;
        });
    }

    private void ResetAlbumArtPlaceholder(AlbumArtTransitionDirection direction = AlbumArtTransitionDirection.None)
    {
        AnimateAlbumArtTransition(direction, () =>
        {
            AlbumArtImage.Source = null;
            AlbumArtImage.Visibility = Visibility.Collapsed;
            _currentArtBrush = null;
            AlbumArtBorder.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
            AlbumArtIcon.Visibility = Visibility.Visible;
        });
        _currentAlbumArt = null;
        _currentAlbumArtBytes = null;
        _currentAlbumArtMimeType = null;
        _currentAlbumArtPictureType = null;

        QueueAlbumArtAppearanceRefresh();
    }

    // Смена обложки в духе iTunes: снимок прежней "улетает" с затуханием, новая "влетает" с противоположной стороны.
    // При удержании Next/Previous длительность 120 ms вместо 460 ms — укладывается в repeat hotkey без накопления ghost-кадров.
    private void AnimateAlbumArtTransition(AlbumArtTransitionDirection direction, Action applyNewArt)
    {
        bool canAnimate = direction != AlbumArtTransitionDirection.None &&
            _settings.AlbumArtTransitionEnabled &&
            !AccessibilityPreferences.ShouldReduceMotion(_settings) && IsLoaded;

        string? oldPath = _lastArtPath;
        string? newPath = _currentTrackPath;
        _lastArtPath = newPath;
        PrefetchNeighborArt(newPath);

        if (!canAnimate)
        {
            ResetAlbumArtTransitionLayers();
            _albumArtTransitionBurstPolicy.Reset();
            applyNewArt();
            RefreshCarouselSides();
            return;
        }

        bool isBurst = _albumArtTransitionBurstPolicy.ShouldSkipAnimation(DateTime.UtcNow);
        int transitionGeneration = ResetAlbumArtTransitionLayers();

        if (IsCarouselTransition)
        {
            ImageSource? oldArt = AlbumArtImage.Visibility == Visibility.Visible ? AlbumArtImage.Source : null;
            applyNewArt();
            ImageSource? newArt = AlbumArtImage.Visibility == Visibility.Visible ? AlbumArtImage.Source : null;
            RunCarouselTransition(direction, oldArt, newArt, oldPath, newPath,
                isBurst ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(460), transitionGeneration);
            return;
        }

        double size = AlbumArtBorder.ActualWidth > 0 ? AlbumArtBorder.ActualWidth : AlbumArtBorder.Width;
        double distance = size + 24;
        double exitX = direction == AlbumArtTransitionDirection.Next ? -distance : distance;
        double enterFromX = direction == AlbumArtTransitionDirection.Next ? distance : -distance;

        // "Призрак" — снимок ТЕКУЩЕЙ (ещё старой) обложки, показанный поверх основной, пока та
        // подменяется на новую и стартует за кадром с противоположной стороны.
        AlbumArtGhostBorder.Width = AlbumArtBorder.Width;
        AlbumArtGhostBorder.Height = AlbumArtBorder.Height;
        AlbumArtGhostBorder.CornerRadius = AlbumArtBorder.CornerRadius;
        AlbumArtGhostImage.Source = AlbumArtImage.Source;
        AlbumArtGhostImage.Visibility = AlbumArtImage.Visibility;
        AlbumArtGhostBorder.Background = AlbumArtImage.Visibility == Visibility.Visible
            ? Brushes.Transparent
            : AlbumArtBorder.Background;
        AlbumArtGhostIcon.Visibility = AlbumArtIcon.Visibility;
        AlbumArtGhostBorder.Opacity = 1;
        AlbumArtGhostBorder.Visibility = Visibility.Visible;

        applyNewArt();
        AlbumArtBorderTransform.X = enterFromX;
        AlbumArtBorderScale.ScaleX = 0.88;
        AlbumArtBorderScale.ScaleY = 0.88;

        // Кривые разные: уезжающая обложка ускоряется (EaseIn), влетающая гасит скорость и мягко садится (EaseOut).
        var duration = isBurst ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(460);
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var enterEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        var ghostSlide = new DoubleAnimation(0, exitX, duration) { EasingFunction = exitEase };
        var ghostScaleAnim = new DoubleAnimation(1, 0.88, duration) { EasingFunction = exitEase };
        var ghostFade = new DoubleAnimation(1, 0, duration) { EasingFunction = exitEase };
        ghostSlide.Completed += (_, _) =>
        {
            if (transitionGeneration == _albumArtTransitionGeneration)
                AlbumArtGhostBorder.Visibility = Visibility.Collapsed;
        };

        var enterSlide = new DoubleAnimation(enterFromX, 0, duration) { EasingFunction = enterEase };
        var enterScaleAnim = new DoubleAnimation(0.88, 1, duration) { EasingFunction = enterEase };

        AlbumArtGhostTransform.BeginAnimation(TranslateTransform.XProperty, ghostSlide);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, ghostScaleAnim);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, ghostScaleAnim);
        AlbumArtGhostBorder.BeginAnimation(OpacityProperty, ghostFade);
        AlbumArtBorderTransform.BeginAnimation(TranslateTransform.XProperty, enterSlide);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, enterScaleAnim);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, enterScaleAnim);
    }

    private int ResetAlbumArtTransitionLayers()
    {
        _albumArtTransitionGeneration++;
        AlbumArtGhostTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AlbumArtGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AlbumArtGhostBorder.BeginAnimation(OpacityProperty, null);
        AlbumArtBorder.BeginAnimation(OpacityProperty, null);
        AlbumArtBorderTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AlbumArtBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        AlbumArtGhostTransform.X = 0;
        AlbumArtGhostScale.ScaleX = 1;
        AlbumArtGhostScale.ScaleY = 1;
        AlbumArtGhostBorder.Opacity = 1;
        AlbumArtGhostBorder.Visibility = Visibility.Collapsed;
        AlbumArtGhostImage.Source = null;
        AlbumArtGhostImage.Visibility = Visibility.Collapsed;
        AlbumArtGhostIcon.Visibility = Visibility.Collapsed;
        AlbumArtBorderTransform.X = 0;
        AlbumArtBorderScale.ScaleX = 1;
        AlbumArtBorderScale.ScaleY = 1;
        AlbumArtBorder.Opacity = 1;
        ClearCarouselLayers();
        return _albumArtTransitionGeneration;
    }

    // Флаг анимации смены обложки (AnimateAlbumArtTransition) читается из _settings при каждом вызове — отдельного применения не нужно.
    public bool IsAlbumArtTransitionEnabled => _settings.AlbumArtTransitionEnabled;

    public void SetAlbumArtTransitionEnabled(bool enabled) => _settings.AlbumArtTransitionEnabled = enabled;
}
