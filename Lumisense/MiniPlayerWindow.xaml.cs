using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Lumisense;

public partial class MiniPlayerWindow : Window
{
    private readonly MainWindow _mainWindow;
    private bool _isDraggingProgress;

    // Тот же паттерн, что в MainWindow.FireAndForget / SettingsWindow.FireAndForget: без него исключение из
    // SaveAsync терялось бы до сборки мусора (TaskScheduler.UnobservedTaskException) или не успевало бы всплыть
    // до закрытия окна.
    private static async void FireAndForget(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка в фоновой операции \"{operationName}\"", ex);
        }
    }

    // HeaderPanel в XAML имеет отступы "10,8,10,2": при видимой полосе прогресса нижний отступ меньше, чтобы утянуть заголовок к бару;
    // без полосы он увеличивается до 10 (см. ApplyProgressBarVisibility).
    private const double HeaderHorizontalMargin = 10;
    private const double HeaderTopMargin = 8;
    private const double HeaderBottomMarginWithProgress = 2;
    private const double HeaderBottomMarginWithoutProgress = 10;

    // Из настроек (см. ApplyProgressBarVisibility) — показывать полосу прогресса сейчас или нет.
    private bool _showProgress = true;

    // Независимая настройка контура прогресса вокруг обложки. Обычная горизонтальная полоса
    // может быть выключена при включённом контуре и наоборот.
    private bool _showArtworkProgress;

    // Angle крутится через AnimationClock, а не storyboard с поиском цели по namescope — так анимация
    // гарантированно живёт на transform обложки даже в отдельном transparent Window.
    private AnimationClock? _vinylRotationClock;

    // Реальный Measure, а не константы под каждую комбинацию видимости строк: Grid с рядами Auto отдаёт лишнее/недостающее
    // место последнему ряду, и константы расходятся с раскладкой.
    private double MeasureContentHeight()
    {
        ContentGrid.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        return ContentGrid.DesiredSize.Height;
    }

    // Прилипание к краям: механика в WindowSnapHelper (WM_MOVING), включается AppSettings.MiniPlayerSnapToEdges
    // (по умолчанию включено — прежнее поведение).

    private static readonly IntPtr HWND_TOPMOST = WindowSnapHelper.HWND_TOPMOST;
    private const uint SWP_NOMOVE = WindowSnapHelper.SWP_NOMOVE;
    private const uint SWP_NOSIZE = WindowSnapHelper.SWP_NOSIZE;
    private const uint SWP_NOACTIVATE = WindowSnapHelper.SWP_NOACTIVATE;

    // Windows иногда молча теряет топмост (флаг остаётся, Z-order нет — после полноэкранных игр, UAC, RDP, блокировки экрана);
    // раз в несколько секунд переустанавливаем окно поверх через Win32 SetWindowPos.
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private IntPtr _hwnd;

    // Снимок на начало перетаскивания (позиция курсора и прямоугольник окна): расчёты идут от него, а не от предыдущего
    // WM_MOVING, иначе прижатое к краю окно не оттащить: позиция — чистое смещение курсора от точки старта.
    private bool _isDragging;
    private WindowSnapHelper.POINT _dragStartCursor;
    private WindowSnapHelper.RECT _dragStartRect;

    // См. ApplyButtonsLayoutMode — true, когда в настройках выбран режим "кнопки на месте
    // обложки" (AppSettings.MiniPlayerButtonsLayout == "Overlay") вместо прежнего "снизу".
    private bool _buttonsOverlayMode;
    private DispatcherTimer? _volumeOverlayRestoreTimer;
    private bool _volumeOverlaySuppressedControls;

    // true, пока MiniOpacityContextValueEditor показан вместо MiniOpacityContextValueText
    // (см. BeginOpacityValueEdit) — гвардит слайдер/фокус от вмешательства во время ввода.
    private bool _isEditingOpacityValue;

    public MiniPlayerWindow(MainWindow mainWindow)
    {
        InitializeComponent();

        _mainWindow = mainWindow;
        ApplyAccessibilityPreferences();
        _mainWindow.TrackInfoChanged += OnTrackInfoChanged;
        _mainWindow.PlaybackState.Changed += OnPlaybackSnapshotChanged;
        _mainWindow.VolumeChanged += OnVolumeChanged;
        _mainWindow.RepeatModeChanged += OnRepeatModeChanged;
        _mainWindow.ShuffleStateChanged += OnShuffleStateChanged;
        FavoritesChangeNotifier.Instance.PropertyChanged += OnFavoritesChanged;

        ApplyButtonsLayoutMode();
        ApplyProgressBarVisibility();
        ApplyArtworkProgressVisibility();
        ApplyArtworkProgressThickness();
        ApplyArtworkProgressColor();
        ApplyArtworkStyle();

        // Сразу отображаем текущие визуальные данные и единый runtime-снимок плеера.
        OnTrackInfoChanged(_mainWindow.CurrentTitle, _mainWindow.CurrentArtist, _mainWindow.CurrentArtBrush);
        OnPlaybackSnapshotChanged(_mainWindow.PlaybackState.Current);
        UpdateSecondaryButton();

        // Название могло быть длинным ещё до открытия мини-плеера — пересчитываем бегущую
        // строку после первого прохода layout, когда TitleClipBorder.ActualWidth уже известен.
        Loaded += (_, _) => UpdateTitleMarquee();

        _topmostTimer.Tick += TopmostTimer_Tick;
        _topmostTimer.Start();
    }

    public void ApplyAccessibilityPreferences() =>
        AccessibilityPreferences.ApplyToWindow(this, _mainWindow.Settings);

    // Возвращаем окно в топмост через Win32, а не полагаемся на Topmost=true (см. _topmostTimer), только при включённом
    // "поверх окон" и не свёрнутом мини-плеере, чтобы не дёргать SetWindowPos впустую.
    private void TopmostTimer_Tick(object? sender, EventArgs e)
    {
        if (!Topmost || _hwnd == IntPtr.Zero || WindowState == WindowState.Minimized) return;

        WindowSnapHelper.SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }


    // Перехватываем Win32-сообщения: только так можно править позицию прямо во время родного DragMove, чтобы
    // прилипание было плавным, а не рывком после отпускания мыши.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
        {
            _hwnd = hwndSource.Handle;
            hwndSource.AddHook(WndProc);
            ApplyTheme();
            ApplyBackground();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WindowSnapHelper.WM_ENTERSIZEMOVE:
                // Начало перетаскивания фиксирует точку отсчёта; GetWindowRect, GetCursorPos и WM_MOVING — в физических пикселях,
                // поэтому расчёт точен и при масштабе 125%/150%.
                _isDragging = true;
                WindowSnapHelper.GetCursorPos(out _dragStartCursor);
                WindowSnapHelper.GetWindowRect(_hwnd, out _dragStartRect);
                break;

            case WindowSnapHelper.WM_MOVING when !_mainWindow.Settings.MiniPlayerPinned
                                                  && _mainWindow.Settings.MiniPlayerSnapToEdges && _isDragging:
                {
                    WindowSnapHelper.GetCursorPos(out var cursor);
                    int dx = cursor.X - _dragStartCursor.X;
                    int dy = cursor.Y - _dragStartCursor.Y;

                    var width = _dragStartRect.Right - _dragStartRect.Left;
                    var height = _dragStartRect.Bottom - _dragStartRect.Top;

                    var rect = new WindowSnapHelper.RECT
                    {
                        Left = _dragStartRect.Left + dx,
                        Top = _dragStartRect.Top + dy,
                    };
                    rect.Right = rect.Left + width;
                    rect.Bottom = rect.Top + height;

                    WindowSnapHelper.SnapToScreenEdges(ref rect);

                    Marshal.StructureToPtr(rect, lParam, false);
                    handled = true;
                    return new IntPtr(1); // приложение обязано вернуть TRUE, если само обработало WM_MOVING
                }

            case WindowSnapHelper.WM_EXITSIZEMOVE:
                _isDragging = false;
                break;

        }

        return IntPtr.Zero;
    }

    // Сырые значения последних OnTrackInfoChanged/OnProgressChanged — UpdateSecondaryLine перерисует вторую строку
    // в любой момент (например, после смены MiniPlayerInfoMode в настройках, см. ApplyInfoModeLive).
    private string _lastArtist = "";
    private double _lastCurrentSeconds;
    private double _lastTotalSeconds;

    private void OnTrackInfoChanged(string title, string artist, Brush? art)
    {
        TitleText.Text = title;
        _lastArtist = artist;

        // RaiseTrackInfoChanged сначала публикует PlaybackSnapshot, так что position/duration уже могут быть восстановлены:
        // не затираем их нулями, иначе первый кадр контура прогресса будет пустым, а следующий тик резко перескочит.
        var snapshot = _mainWindow.PlaybackState.Current;
        bool snapshotBelongsToTrack = snapshot.DurationSeconds > 0
            && string.Equals(snapshot.Title, title, StringComparison.Ordinal)
            && string.Equals(snapshot.Artist, artist, StringComparison.Ordinal);
        _lastCurrentSeconds = snapshotBelongsToTrack ? snapshot.PositionSeconds : 0;
        _lastTotalSeconds = snapshotBelongsToTrack ? snapshot.DurationSeconds : 0;
        UpdateSecondaryLine();

        // Новый трек — новое состояние избранного: в режиме "Избранное" (SecondaryButtonMode) сердечко сразу отражает новый трек.
        if (SecondaryButtonMode == "Favorite") UpdateFavoriteSecondaryButtonVisual();

        if (art is ImageBrush { ImageSource: not null } imageBrush)
        {
            // Не используем ImageBrush как Background: WPF может выбрать низкокачественное масштабирование; Image ниже рендерится
            // с HighQuality и служит одним слоем для обычного и винилового оформления.
            ArtImage.Source = imageBrush.ImageSource;
            ArtImage.Visibility = Visibility.Visible;
            ArtBorder.Background = Brushes.Transparent;
            ArtIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtImage.Source = null;
            ArtImage.Visibility = Visibility.Collapsed;
            ArtBorder.Background = art ?? (Brush)FindResource("ControlFillColorSecondaryBrush");
            ArtIcon.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
        }

        // Если snapshot уже содержит восстановленные position/duration, сразу рисуем их.
        // Нулевой прогресс нужен только для действительно нового трека без готовой длительности.
        UpdateArtworkProgressOutline(_lastCurrentSeconds, _lastTotalSeconds);
        UpdateTitleMarquee();
    }

    // Вторая строка заголовка (режим — AppSettings.MiniPlayerInfoMode, страница "Мини-плеер"); вызывается на каждое обновление
    // трека/прогресса и сразу при смене настройки (ApplyInfoModeLive), как UpdateSecondaryButton/ApplyButtonsLayoutMode.
    private void UpdateSecondaryLine()
    {
        switch (_mainWindow.Settings.MiniPlayerInfoMode)
        {
            case "TitleOnly":
                ArtistText.Visibility = Visibility.Collapsed;
                break;

            case "TitleRemaining":
                ArtistText.Visibility = Visibility.Visible;
                ArtistText.Text = FormatRemaining(_lastCurrentSeconds, _lastTotalSeconds);
                break;

            default: // "TitleArtist"
                ArtistText.Visibility = Visibility.Visible;
                ArtistText.Text = _lastArtist;
                break;
        }
    }

    private static string FormatRemaining(double currentSeconds, double totalSeconds)
    {
        if (totalSeconds <= 0) return "—";

        var remaining = TimeSpan.FromSeconds(Math.Max(totalSeconds - currentSeconds, 0));
        return $"-{remaining:mm\\:ss} осталось";
    }

    // См. UpdateSecondaryLine — публичный вызов для MainWindow.ApplyMiniPlayerInfoModeLive.
    public void ApplyInfoModeLive() => UpdateSecondaryLine();

    // От Win32-блюра (ACCENT_ENABLE_ACRYLICBLURBEHIND) отказались: он конфликтовал с AllowsTransparency="True" и
    // ломал ползунок прозрачности. Теперь RootBorder — SolidColorBrush (MiniBackgroundBrush), альфа = прозрачность.

    // Базовые RGB фона (альфа добавляется в ApplyBackground); светлая тема — светло-серая, а не белая: на
    // полупрозрачном белом поверх произвольного рабочего стола тёмный текст читается плохо.
    private static readonly (byte R, byte G, byte B) DarkBackgroundRgb = (0x1C, 0x1C, 0x1E);
    private static readonly (byte R, byte G, byte B) LightBackgroundRgb = (0xF2, 0xF2, 0xF2);

    private bool _isLightTheme;
    private bool _overlayCompatibilityMode;

    // Кисти получаются через FindResource по x:Key и кэшируются один раз, чтобы не искать
    // их в дереве ресурсов при каждом обновлении темы/прозрачности
    private SolidColorBrush? _textPrimaryBrush;
    private SolidColorBrush? _textSecondaryBrush;
    private SolidColorBrush? _controlFillBrush;
    private SolidColorBrush? _controlFillSecondaryBrush;
    private SolidColorBrush? _controlStrongFillBrush;
    private SolidColorBrush? _controlStrokeBrush;

    // Пересчитывает цвета, зависящие от темы приложения: при открытии (OnSourceInitialized) и при смене темы на
    // открытом мини-плеере (ApplyThemeLive / MainWindow.ApplyMiniPlayerThemeLive).
    private void ApplyTheme()
    {
        _textPrimaryBrush ??= (SolidColorBrush)FindResource("TextFillColorPrimaryBrush");
        _textSecondaryBrush ??= (SolidColorBrush)FindResource("TextFillColorSecondaryBrush");
        _controlFillBrush ??= (SolidColorBrush)FindResource("ControlFillColorDefaultBrush");
        _controlFillSecondaryBrush ??= (SolidColorBrush)FindResource("ControlFillColorSecondaryBrush");
        _controlStrongFillBrush ??= (SolidColorBrush)FindResource("ControlStrongFillColorDefaultBrush");
        _controlStrokeBrush ??= (SolidColorBrush)FindResource("ControlStrokeColorDefaultBrush");

        _isLightTheme = _mainWindow.Settings.IsLightThemeResolved();

        if (_isLightTheme)
        {
            _textPrimaryBrush.Color = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
            _textSecondaryBrush.Color = Color.FromArgb(0xB0, 0x1A, 0x1A, 0x1A);
            _controlFillBrush.Color = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
            _controlFillSecondaryBrush.Color = Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
            _controlStrongFillBrush.Color = Color.FromArgb(0x30, 0x00, 0x00, 0x00);
            _controlStrokeBrush.Color = Color.FromArgb(0x26, 0x00, 0x00, 0x00);
        }
        else
        {
            _textPrimaryBrush.Color = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            _textSecondaryBrush.Color = Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF);
            _controlFillBrush.Color = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
            _controlFillSecondaryBrush.Color = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
            _controlStrongFillBrush.Color = Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF);
            _controlStrokeBrush.Color = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
        }

        ApplyBackground();
    }

    // Альфа-канал фона — это и есть настройка "прозрачность мини-плеера" (0.3..1.0 в UI, см.
    // MiniOpacitySlider), базовый RGB берётся из текущей темы (см. ApplyTheme).
    private void ApplyBackground()
    {
        byte alpha = _overlayCompatibilityMode
            ? (byte)255
            : (byte)Math.Round(Math.Clamp(_mainWindow.Settings.MiniPlayerOpacity, 0.0, 1.0) * 255);
        var rgb = _isLightTheme ? LightBackgroundRgb : DarkBackgroundRgb;
        MiniBackgroundBrush.Color = Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }

    // Вызывается из MainWindow.ApplyMiniPlayerOpacityLive, когда пользователь двигает
    // слайдер прозрачности в окне настроек, пока мини-плеер уже открыт.
    public void ApplyOpacityLive() => ApplyBackground();

    // Ручной режим для игр/Steam Overlay. Не меняет настройки пользователя, а временно
    // делает слой мини-плеера плотным и останавливает декоративную rotation-анимацию.
    public void ApplyOverlayCompatibilityLive(bool enabled)
    {
        _overlayCompatibilityMode = enabled;
        if (enabled)
            StopVinylRotation();
        else
            UpdateVinylRotation(_mainWindow.IsPlayingNow);

        ApplyBackground();
    }

    // Вызывается из MainWindow.ApplyMiniPlayerThemeLive, когда пользователь переключает
    // светлую/тёмную тему в настройках, пока мини-плеер уже открыт.
    public void ApplyThemeLive() => ApplyTheme();

    // Default — скруглённая квадратная обложка; Vinyl и StaticCircle — круг, вращается только Vinyl во время воспроизведения;
    // индикатор прогресса остаётся отдельным неподвижным слоем под обложкой.
    public void ApplyArtworkStyle()
    {
        string style = _mainWindow.Settings.MiniPlayerArtworkStyle;
        bool circle = string.Equals(style, "Vinyl", StringComparison.Ordinal)
                      || string.Equals(style, "StaticCircle", StringComparison.Ordinal);
        bool rotatingVinyl = string.Equals(style, "Vinyl", StringComparison.Ordinal);
        ArtBorder.CornerRadius = circle ? new CornerRadius(21) : new CornerRadius(8);
        ApplyArtworkImageClip(circle);
        ApplyArtworkProgressClip(circle);
        ApplyArtworkProgressThickness();

        if (!rotatingVinyl)
        {
            StopVinylRotation();
            return;
        }

        EnsureVinylRotation();
        UpdateVinylRotation(_mainWindow.IsPlayingNow);
    }

    private void ApplyArtworkImageClip(bool circle)
    {
        // Border.CornerRadius не обрезает дочерний Image. Поэтому форма задаётся самому
        // пиксельному слою: скруглённый квадрат или круглая обложка.
        ArtImage.Clip = circle
            ? new EllipseGeometry(new Point(21, 21), 21, 21)
            : new RectangleGeometry(new Rect(0, 0, 42, 42), 8, 8);
    }

    private void ApplyArtworkProgressClip(bool circle)
    {
        // Контур лежит под обложкой и не обрезается по 42×42: внешний край закрывает антиалиасинговые пиксели изображения,
        // а внутренняя часть полностью закрыта ArtBorder.
        ArtProgressOutline.Clip = null;
    }

    private void EnsureVinylRotation()
    {
        if (_vinylRotationClock is not null) return;

        var rotation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(18)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _vinylRotationClock = (AnimationClock)rotation.CreateClock(true);
        ArtRotateTransform.ApplyAnimationClock(RotateTransform.AngleProperty, _vinylRotationClock,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateVinylRotation(bool isPlaying)
    {
        if (_overlayCompatibilityMode
            || !string.Equals(_mainWindow.Settings.MiniPlayerArtworkStyle, "Vinyl", StringComparison.Ordinal))
            return;

        EnsureVinylRotation();
        if (isPlaying)
            _vinylRotationClock!.Controller?.Resume();
        else
            _vinylRotationClock!.Controller?.Pause();
    }

    private void StopVinylRotation()
    {
        ArtRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        _vinylRotationClock = null;
        ArtRotateTransform.Angle = 0;
    }

    // Бегущая строка: название статично, пока помещается в 140px, иначе бесконечная анимация X с постоянной скоростью (px/сек).
    // Ширина — Measure() у TitleText (FormattedText разрешал шрифт иначе и обрывал прокрутку); MarqueeEndBufferPx — запас.
    private const double MarqueePixelsPerSecond = 34.0;
    private const double MarqueeEdgePauseSeconds = 1.0;
    private const double DefaultTitleClipWidth = 120.0;
    private const double MarqueeEndBufferPx = 3.0;

    private void UpdateTitleMarquee()
    {
        TitleTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        TitleTranslate.X = 0;

        if (string.IsNullOrEmpty(TitleText.Text)) return;

        double clipWidth = TitleClipBorder.ActualWidth > 0 ? TitleClipBorder.ActualWidth : DefaultTitleClipWidth;

        TitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = TitleText.DesiredSize.Width;

        double distance = textWidth - clipWidth;
        if (distance <= 0) return; // помещается целиком — статичный текст, анимация не нужна

        distance += MarqueeEndBufferPx;

        var scrollDuration = TimeSpan.FromSeconds(distance / MarqueePixelsPerSecond);
        var pause = TimeSpan.FromSeconds(MarqueeEdgePauseSeconds);

        var t0 = TimeSpan.Zero;
        var t1 = t0 + pause;              // конец паузы у начала строки
        var t2 = t1 + scrollDuration;     // доехали до конца строки
        var t3 = t2 + pause;              // конец паузы у конца строки
        var t4 = t3 + scrollDuration;     // вернулись в начало

        var keyFrames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t0)));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t1)));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t2)));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t3)));
        keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t4)));

        TitleTranslate.BeginAnimation(TranslateTransform.XProperty, keyFrames);
    }

    // Индикатор громкости показывается при любом изменении, пока открыт мини-плеер (у него нет ползунка); каждый вызов
    // перезапускает Storyboard, поэтому быстрые нажатия хоткея продлевают показ, а не мигают.
    private void OnVolumeChanged(double volume)
    {
        VolumeIndicatorText.Text = $"{(int)Math.Round(volume * 100)}%";
        VolumeIndicatorIcon.Icon = volume <= 0.0 ? "IconSpeakerMute" : "IconSpeaker";

        if (_buttonsOverlayMode)
        {
            // В Overlay-режиме ControlsPanel занимает ту же строку, что HeaderPanel: на время индикатора громкости
            // убираем кнопки, чтобы элементы не перекрывались.
            _volumeOverlaySuppressedControls = ControlsPanel.Visibility == Visibility.Visible;
            if (_volumeOverlaySuppressedControls)
                ControlsPanel.Visibility = Visibility.Collapsed;
        }

        _volumeOverlayRestoreTimer?.Stop();
        _volumeOverlayRestoreTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(1350)
        };
        _volumeOverlayRestoreTimer.Tick += VolumeOverlayRestoreTimer_Tick;
        _volumeOverlayRestoreTimer.Start();

        if (_overlayCompatibilityMode)
        {
            VolumeIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            VolumeIndicator.Opacity = 1;
            return;
        }

        var storyboard = (Storyboard)FindResource("VolumeIndicatorStoryboard");
        storyboard.Begin(this, true);
    }

    private void VolumeOverlayRestoreTimer_Tick(object? sender, EventArgs e)
    {
        if (_volumeOverlayRestoreTimer is not null)
        {
            _volumeOverlayRestoreTimer.Stop();
            _volumeOverlayRestoreTimer.Tick -= VolumeOverlayRestoreTimer_Tick;
            _volumeOverlayRestoreTimer = null;
        }

        // OnVolumeChanged в overlay-режиме пропускает VolumeIndicatorStoryboard, а больше
        // ничего Opacity=1 обратно не сбрасывало — индикатор зависал навсегда.
        if (_overlayCompatibilityMode)
        {
            VolumeIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            VolumeIndicator.Opacity = 0;
        }

        if (!_volumeOverlaySuppressedControls || !_buttonsOverlayMode) return;
        _volumeOverlaySuppressedControls = false;

        if (RootBorder.IsMouseOver)
        {
            ControlsPanel.Visibility = Visibility.Visible;
            HeaderPanel.Visibility = Visibility.Hidden;
            HeaderPanel.IsHitTestVisible = false;
        }
    }

    private void OnPlaybackSnapshotChanged(PlaybackSnapshot snapshot)
    {
        OnPlaybackStateChanged(snapshot.IsPlaying);
        OnProgressChanged(snapshot.PositionSeconds, snapshot.DurationSeconds);
    }

    private void OnProgressChanged(double currentSeconds, double totalSeconds)
    {
        _lastCurrentSeconds = currentSeconds;
        _lastTotalSeconds = totalSeconds;
        if (_mainWindow.Settings.MiniPlayerInfoMode == "TitleRemaining") UpdateSecondaryLine();
        if (_isDraggingProgress || totalSeconds <= 0) return;

        double ratio = Math.Clamp(currentSeconds / totalSeconds, 0.0, 1.0);
        double trackWidth = Math.Max(ActualWidth - 20, 0); // 20 = отступы слева/справа (10+10)
        ProgressFill.Width = trackWidth * ratio;
        UpdateArtworkProgressOutline(ratio);
    }


    private void OnPlaybackStateChanged(bool isPlaying)
    {
        PlayPauseButton.Icon = IconResources.MakeOnAccent(isPlaying ? "IconPause" : "IconPlay");
        PlayPauseButton.Background = new SolidColorBrush(_mainWindow.GetResolvedAccentColor()); // всегда акцентная
        UpdateVinylRotation(isPlaying);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalNext();
    private void PrevButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalPrev();
    private void RestoreButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExitMiniMode();
    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _mainWindow.ShowSettingsWindow("MiniPlayer");
    private void NowPlayingMenuItem_Click(object sender, RoutedEventArgs e) => _mainWindow.ShowNowPlayingWindow();

    // Не полагаемся на ControlAppearance.Primary WPF-UI: тот же баг, что в MainWindow.SetAccentButtonActive (фон не обновляется
    // при смене акцента), поэтому красим Background вручную.
    private void SetAccentButtonActive(Wpf.Ui.Controls.Button button, bool active)
    {
        button.Appearance = ControlAppearance.Secondary;

        if (active)
            button.Background = new SolidColorBrush(_mainWindow.GetResolvedAccentColor());
        else
            button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
    }

    // В компактном мини-плеере места хватает лишь на одну "вторую" кнопку (повтор/перемешать/сердечко); какую функцию
    // она выполняет — AppSettings.MiniPlayerSecondaryButton (страница "Мини-плеер"), SecondaryButton в разметке один на все.
    private string SecondaryButtonMode => _mainWindow.Settings.MiniPlayerSecondaryButton;

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        switch (SecondaryButtonMode)
        {
            case "Shuffle":
                _mainWindow.ExternalToggleShuffle();
                break;
            case "Favorite":
                _mainWindow.ExternalToggleFavoriteCurrentTrack();
                break;
            default:
                _mainWindow.ExternalToggleRepeat();
                break;
        }
    }

    // Синхронизирует вид кнопки повтора с режимом в основном окне (как MainWindow.SetRepeatMode, но меньше); только если
    // выбрана функция "Повтор" — иначе UpdateSecondaryButton сама подставит актуальный режим при переключении.
    private void OnRepeatModeChanged(string modeName)
    {
        if (SecondaryButtonMode != "Repeat") return;

        switch (modeName)
        {
            case "All":
                SecondaryButton.Icon = IconResources.MakeOnAccent("IconRepeatAll", size: 12);
                SetAccentButtonActive(SecondaryButton, true);
                break;
            case "One":
                SecondaryButton.Icon = IconResources.MakeOnAccent("IconRepeatOne", size: 12);
                SetAccentButtonActive(SecondaryButton, true);
                break;
            default:
                SecondaryButton.Icon = IconResources.Make("IconRepeatAll", size: 12);
                SetAccentButtonActive(SecondaryButton, false);
                break;
        }
    }

    // Зеркальный аналог OnRepeatModeChanged для перемешивания — применяется, только если
    // сейчас выбрана функция "Перемешать" (см. SecondaryButtonMode), по той же причине.
    private void OnShuffleStateChanged(bool enabled)
    {
        if (SecondaryButtonMode != "Shuffle") return;

        SecondaryButton.Icon = enabled
            ? IconResources.MakeOnAccent("IconShuffle", size: 12)
            : IconResources.Make("IconShuffle", size: 12);
        SetAccentButtonActive(SecondaryButton, enabled);
    }

    // Избранное как вторая кнопка: у FavoritesManager нет события на MainWindow, изменения идут через FavoritesChangeNotifier
    // (см. Favorites.cs); подписка безусловна — проверка режима внутри дешевле переподписки при смене настройки.
    private void OnFavoritesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (SecondaryButtonMode != "Favorite") return;
        UpdateFavoriteSecondaryButtonVisual();
    }

    private void UpdateFavoriteSecondaryButtonVisual()
    {
        bool isFavorite = _mainWindow.CurrentTrackPath is { } path && FavoritesManager.IsFavorite(path);

        SecondaryButton.Icon = isFavorite
            ? IconResources.MakeOnAccent("IconHeartFilled", size: 12)
            : IconResources.Make("IconHeart", size: 12);
        SetAccentButtonActive(SecondaryButton, isFavorite);
    }

    // Вызывается при открытии и при смене настройки "какую функцию показывать" на открытом мини-плеере
    // (MainWindow.ApplyMiniPlayerSecondaryButtonLive): перерисовывает SecondaryButton по состоянию основного окна.
    public void UpdateSecondaryButton()
    {
        switch (SecondaryButtonMode)
        {
            case "Shuffle":
                OnShuffleStateChanged(_mainWindow.CurrentIsShuffleEnabled);
                break;
            case "Favorite":
                UpdateFavoriteSecondaryButtonVisual();
                break;
            default:
                OnRepeatModeChanged(_mainWindow.CurrentRepeatModeName);
                break;
        }
    }

    // Вызывается из MainWindow.RefreshAccentDependentIcons: SetAccentButtonActive/OnPlaybackStateChanged красят кнопки только
    // при смене состояния, а здесь нужно перекрасить их, когда сменился лишь цвет акцента.
    public void RefreshAccentButtons()
    {
        ApplyContextMenuAccent();
        PlayPauseButton.Background = new SolidColorBrush(_mainWindow.GetResolvedAccentColor()); // всегда акцентная

        bool secondaryActive = SecondaryButtonMode switch
        {
            "Shuffle" => _mainWindow.CurrentIsShuffleEnabled,
            "Favorite" => _mainWindow.CurrentTrackPath is { } path && FavoritesManager.IsFavorite(path),
            _ => _mainWindow.CurrentRepeatModeName != "Off"
        };

        if (secondaryActive)
            SecondaryButton.Background = new SolidColorBrush(_mainWindow.GetResolvedAccentColor());
    }

    // Пока true — MiniOpacityContextSlider.Value выставляется программно (MiniPlayerContextMenu_Opened): ValueChanged
    // должен промолчать, чтобы не применять настройку повторно и не зациклить обновления с окном настроек.
    private bool _isSyncingOpacitySlider;

    // Идёт перетаскивание прозрачным Border поверх MiniOpacityContextSlider (как _isDraggingOpacityOverlay в SettingsWindow).
    private bool _isDraggingOpacityOverlay;

    // Контекстные скорость и тон получают программные значения при открытии меню. Этот флаг
    // не даёт ValueChanged применить уже существующие настройки повторно.
    private bool _isSyncingPlaybackContextSliders;
    private bool _isDraggingPlaybackRateOverlay;
    private bool _isDraggingPlaybackPitchOverlay;

    private void MiniPlayerContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // На случай, если меню открывается заново, пока предыдущее редактирование почему-то не
        // закрылось штатно (см. MiniPlayerContextMenu_Closed) — начинаем с чистого состояния.
        CancelOpacityValueEdit();

        ApplyContextMenuAccent();
        SyncContextMenuToggleStates();

        // App.xaml локализует ContextMenu на том же Opened, и шаблон MenuItem иногда успевал отрисовать пустое состояние;
        // второй проход на ContextIdle закрепляет значение после layout/локализации.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(SyncContextMenuToggleStates));

        _isSyncingOpacitySlider = true;
        MiniOpacityContextSlider.Value = _mainWindow.Settings.MiniPlayerOpacity;
        MiniOpacityContextValueText.Text = $"{(int)Math.Round(_mainWindow.Settings.MiniPlayerOpacity * 100)}%";
        _isSyncingOpacitySlider = false;

        _isSyncingPlaybackContextSliders = true;
        try
        {
            MiniPlaybackRateContextSlider.Value = Math.Clamp(_mainWindow.Settings.PlaybackSpeed, 0.5, 2.0);
            MiniPlaybackRateContextValueText.Text = FormatContextPlaybackRate(MiniPlaybackRateContextSlider.Value);
            MiniPlaybackPitchContextSlider.Value = Math.Clamp(_mainWindow.Settings.PlaybackPitchSemitones, -12.0, 12.0);
            MiniPlaybackPitchContextValueText.Text = FormatContextPlaybackPitch(MiniPlaybackPitchContextSlider.Value);
            UpdateSecondaryContextButtons();
        }
        finally
        {
            _isSyncingPlaybackContextSliders = false;
        }
    }

    // Меню закрылось (клик мимо, выбор, Escape) — коммитим редактирование; до этого TextBox держит фокус, и движение мыши
    // по другим пунктам его не сбивает (см. MiniOpacityContextValueEditor_LostKeyboardFocus).
    private void MiniPlayerContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (_isEditingOpacityValue) CommitOpacityValueEdit();
    }

    // Popup-контекст WPF — отдельное дерево ресурсов: toggle в меню берёт accent только из MiniPlayerMenuAccentBrush,
    // поэтому не откатывается к системному цвету Windows после смены темы или повторного открытия.
    private void ApplyContextMenuAccent()
    {
        Color accent = _mainWindow.GetResolvedAccentColor();
        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        var contrastBrush = new SolidColorBrush(GetContextMenuAccentContrast(accent));
        contrastBrush.Freeze();

        MiniPlayerContextMenu.Resources["SystemAccentColor"] = accent;
        MiniPlayerContextMenu.Resources["AccentFillColorDefaultBrush"] = accentBrush;
        MiniPlayerContextMenu.Resources["AccentFillColorSecondaryBrush"] = accentBrush;
        MiniPlayerContextMenu.Resources["AccentTextFillColorPrimaryBrush"] = accentBrush;
        MiniPlayerContextMenu.Resources["TextOnAccentFillColorPrimaryBrush"] = contrastBrush;

        // Точные DynamicResource-ключи шаблона WPF-UI CheckBox 3.0.5: без переопределения popup ContextMenu берёт их из
        // системной темы Windows, а не из акцента Lumisense.
        MiniPlayerContextMenu.Resources["CheckBoxCheckBackgroundFillChecked"] = accentBrush;
        MiniPlayerContextMenu.Resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = accentBrush;
        MiniPlayerContextMenu.Resources["CheckBoxCheckBorderBrush"] = accentBrush;
        MiniPlayerContextMenu.Resources["CheckBoxCheckGlyphForeground"] = contrastBrush;
    }

    private static Color GetContextMenuAccentContrast(Color color)
    {
        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance > 0.56 ? Colors.Black : Colors.White;
    }

    private void SyncContextMenuToggleStates()
    {
        PinnedMenuItem.IsCheckable = true;
        TopmostMenuItem.IsCheckable = true;
        OverlayCompatibilityMenuItem.IsCheckable = true;
        PinnedMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerPinned;
        TopmostMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerAlwaysOnTop;
        OverlayCompatibilityMenuItem.IsChecked = _mainWindow.Settings.GameOverlayCompatibilityMode;

        MiniContextSnapToEdgesMenuItem.IsCheckable = true;
        MiniContextShowProgressMenuItem.IsCheckable = true;
        MiniContextShowArtworkProgressMenuItem.IsCheckable = true;
        MiniContextSnapToEdgesMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerSnapToEdges;
        MiniContextShowProgressMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerShowProgress;
        MiniContextShowArtworkProgressMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerShowArtworkProgress;
        MiniContextArtworkStyleMenuItem.Header = "Обложка: " + ArtworkStyleLabel(_mainWindow.Settings.MiniPlayerArtworkStyle);
        MiniContextButtonsLayoutMenuItem.Header = "Кнопки: " +
            (_mainWindow.Settings.MiniPlayerButtonsLayout == "Overlay" ? "поверх обложки" : "снизу");
    }

    private static string ArtworkStyleLabel(string style) => style switch
    {
        "Vinyl" => "винил",
        "StaticCircle" => "круг",
        _ => "обычная"
    };

    private void SnapToEdgesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.Settings.MiniPlayerSnapToEdges = MiniContextSnapToEdgesMenuItem.IsChecked;
        FireAndForget(SettingsManager.SaveAsync(_mainWindow.Settings), "SaveSettingsAsync");
    }

    private void ShowProgressMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.Settings.MiniPlayerShowProgress = MiniContextShowProgressMenuItem.IsChecked;
        _mainWindow.ApplyMiniPlayerProgressBarVisibilityLive();
        FireAndForget(SettingsManager.SaveAsync(_mainWindow.Settings), "SaveSettingsAsync");
    }

    private void ShowArtworkProgressMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.Settings.MiniPlayerShowArtworkProgress = MiniContextShowArtworkProgressMenuItem.IsChecked;
        _mainWindow.ApplyMiniPlayerArtworkProgressVisibilityLive();
        FireAndForget(SettingsManager.SaveAsync(_mainWindow.Settings), "SaveSettingsAsync");
    }

    // Клик по кругу переключает на следующий из 3 стилей — компактнее вложенного подменю.
    private void ArtworkStyleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.Settings.MiniPlayerArtworkStyle = _mainWindow.Settings.MiniPlayerArtworkStyle switch
        {
            "Default" => "Vinyl",
            "Vinyl" => "StaticCircle",
            _ => "Default"
        };
        MiniContextArtworkStyleMenuItem.Header = "Обложка: " + ArtworkStyleLabel(_mainWindow.Settings.MiniPlayerArtworkStyle);
        _mainWindow.ApplyMiniPlayerArtworkStyleLive();
        FireAndForget(SettingsManager.SaveAsync(_mainWindow.Settings), "SaveSettingsAsync");
    }

    private void ButtonsLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.Settings.MiniPlayerButtonsLayout =
            _mainWindow.Settings.MiniPlayerButtonsLayout == "Overlay" ? "Below" : "Overlay";
        MiniContextButtonsLayoutMenuItem.Header = "Кнопки: " +
            (_mainWindow.Settings.MiniPlayerButtonsLayout == "Overlay" ? "поверх обложки" : "снизу");
        _mainWindow.ApplyMiniPlayerButtonsLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_mainWindow.Settings), "SaveSettingsAsync");
    }

    private void MiniSecondaryContextButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { Tag: string mode }) return;

        _mainWindow.SetMiniPlayerSecondaryButtonMode(mode);
        UpdateSecondaryContextButtons();
        e.Handled = true;
    }

    private void UpdateSecondaryContextButtons()
    {
        string mode = SecondaryButtonMode;
        SetAccentButtonActive(MiniSecondaryRepeatContextButton, mode == "Repeat");
        SetAccentButtonActive(MiniSecondaryShuffleContextButton, mode == "Shuffle");
        SetAccentButtonActive(MiniSecondaryFavoriteContextButton, mode == "Favorite");
    }

    private static string FormatContextPlaybackRate(double rate) => $"{rate:0.00}×";

    private static string FormatContextPlaybackPitch(double semitones) => $"{semitones:+0;-0;0} st";

    private void MiniPlaybackRateContextSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mainWindow == null || _isSyncingPlaybackContextSliders) return;

        MiniPlaybackRateContextValueText.Text = FormatContextPlaybackRate(e.NewValue);
        _mainWindow.SetPlaybackRateFromMiniPlayer(e.NewValue);
    }

    private void MiniPlaybackPitchContextSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mainWindow == null || _isSyncingPlaybackContextSliders) return;

        MiniPlaybackPitchContextValueText.Text = FormatContextPlaybackPitch(e.NewValue);
        _mainWindow.SetPlaybackPitchFromMiniPlayer(e.NewValue);
    }

    private void MiniPlaybackRateContextOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        if (e.ClickCount >= 2)
        {
            MiniPlaybackRateContextSlider.Value = 1.0;
            e.Handled = true;
            return;
        }

        overlay.CaptureMouse();
        _isDraggingPlaybackRateOverlay = true;
        UpdateContextSliderFromMouse(MiniPlaybackRateContextSlider, e.GetPosition(overlay).X, overlay.ActualWidth, 0.05);
        e.Handled = true;
    }

    private void MiniPlaybackRateContextOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingPlaybackRateOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateContextSliderFromMouse(MiniPlaybackRateContextSlider, e.GetPosition(overlay).X, overlay.ActualWidth, 0.05);
    }

    private void MiniPlaybackRateContextOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingPlaybackRateOverlay = false;
        e.Handled = true;
    }

    private void MiniPlaybackRateContextOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        MiniPlaybackRateContextSlider.Value = Math.Clamp(
            MiniPlaybackRateContextSlider.Value + Math.Sign(e.Delta) * 0.05,
            MiniPlaybackRateContextSlider.Minimum,
            MiniPlaybackRateContextSlider.Maximum);
        e.Handled = true;
    }

    private void MiniPlaybackPitchContextOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        if (e.ClickCount >= 2)
        {
            MiniPlaybackPitchContextSlider.Value = 0.0;
            e.Handled = true;
            return;
        }

        overlay.CaptureMouse();
        _isDraggingPlaybackPitchOverlay = true;
        UpdateContextSliderFromMouse(MiniPlaybackPitchContextSlider, e.GetPosition(overlay).X, overlay.ActualWidth, 1.0);
        e.Handled = true;
    }

    private void MiniPlaybackPitchContextOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingPlaybackPitchOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateContextSliderFromMouse(MiniPlaybackPitchContextSlider, e.GetPosition(overlay).X, overlay.ActualWidth, 1.0);
    }

    private void MiniPlaybackPitchContextOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingPlaybackPitchOverlay = false;
        e.Handled = true;
    }

    private void MiniPlaybackPitchContextOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        MiniPlaybackPitchContextSlider.Value = Math.Clamp(
            MiniPlaybackPitchContextSlider.Value + Math.Sign(e.Delta),
            MiniPlaybackPitchContextSlider.Minimum,
            MiniPlaybackPitchContextSlider.Maximum);
        e.Handled = true;
    }

    private static void UpdateContextSliderFromMouse(System.Windows.Controls.Slider slider, double positionX,
        double width, double tick)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(positionX / width, 0.0, 1.0);
        double raw = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        slider.Value = Math.Clamp(Math.Round(raw / tick) * tick, slider.Minimum, slider.Maximum);
    }

    private void PinnedMenuItem_Click(object sender, RoutedEventArgs e)
        => _mainWindow.SetMiniPlayerPinned(PinnedMenuItem.IsChecked);

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
        => _mainWindow.SetMiniPlayerTopmost(TopmostMenuItem.IsChecked);

    private void OverlayCompatibilityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.Settings.GameOverlayCompatibilityMode = OverlayCompatibilityMenuItem.IsChecked;
        _mainWindow.ApplyMiniPlayerOverlayCompatibilityLive(_mainWindow.EffectiveGameOverlayCompatibilityEnabled);
        FireAndForget(SettingsManager.SaveAsync(_mainWindow.Settings), "SaveSettingsAsync");
    }

    // Прозрачный Border поверх MiniOpacityContextSlider (IsHitTestVisible="False") считает значение по X клика/перетаскивания
    // (как в SettingsWindow); в Popup ContextMenu нативный захват мыши Thumb нестабилен, Border.CaptureMouse() надёжнее.
    private void MiniOpacityContextOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingOpacityOverlay = true;
        UpdateOpacitySliderFromMouse(e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void MiniOpacityContextOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingOpacityOverlay) return;
        var overlay = (FrameworkElement)sender;
        UpdateOpacitySliderFromMouse(e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void MiniOpacityContextOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingOpacityOverlay = false;
    }

    private void MiniOpacityContextOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        MiniOpacityContextSlider.Value = Math.Clamp(
            MiniOpacityContextSlider.Value + Math.Sign(e.Delta) * 0.05,
            MiniOpacityContextSlider.Minimum,
            MiniOpacityContextSlider.Maximum);
        e.Handled = true;
    }

    private void UpdateOpacitySliderFromMouse(double positionX, double width)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(positionX / width, 0.0, 1.0);
        MiniOpacityContextSlider.Value = MiniOpacityContextSlider.Minimum
            + ratio * (MiniOpacityContextSlider.Maximum - MiniOpacityContextSlider.Minimum);
    }

    private void MiniOpacityContextSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Может сработать ещё внутри InitializeComponent(), до _mainWindow = mainWindow: RangeBase.OnMinimumChanged коэрсит Value
        // и синхронно поднимает ValueChanged. Value="1.0" в XAML убирает причину, проверка _mainWindow — страховка.
        if (_mainWindow == null) return;
        if (_isSyncingOpacitySlider) return;

        if (!_isEditingOpacityValue)
            MiniOpacityContextValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        _mainWindow.SetMiniPlayerOpacity(e.NewValue);
    }

    private void MiniOpacityContextValueText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isEditingOpacityValue)
        {
            e.Handled = true;
            return;
        }

        BeginOpacityValueEdit();
        e.Handled = true;
    }

    private void BeginOpacityValueEdit()
    {
        _isEditingOpacityValue = true;

        int percent = (int)Math.Round(MiniOpacityContextSlider.Value * 100);
        MiniOpacityContextValueEditor.Text = percent.ToString();
        MiniOpacityContextValueText.Visibility = Visibility.Collapsed;
        MiniOpacityContextValueEditor.Visibility = Visibility.Visible;

        MiniOpacityContextValueEditor.Focus();
        Keyboard.Focus(MiniOpacityContextValueEditor);
        MiniOpacityContextValueEditor.SelectAll();
    }

    private void MiniOpacityContextValueEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitOpacityValueEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelOpacityValueEdit();
            e.Handled = true;
        }
    }

    // MenuItem при наведении штатно забирает фокус (без переписывания шаблона не отключить), коммитить тут нельзя:
    // возвращаем фокус TextBox через Dispatcher; коммит — по Enter, Escape или закрытию меню (…_Closed).
    private void MiniOpacityContextValueEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isEditingOpacityValue) return;

        if (e.NewFocus is not DependencyObject newFocus || !IsInsideContextMenu(newFocus))
        {
            CommitOpacityValueEdit();
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isEditingOpacityValue && MiniOpacityContextValueEditor.IsVisible)
            {
                MiniOpacityContextValueEditor.Focus();
                Keyboard.Focus(MiniOpacityContextValueEditor);
            }
        }), DispatcherPriority.Input);
    }

    // ContextMenu живёт в отдельном Popup-дереве — идём вверх по Visual/Logical-цепочке, пока не
    // найдём его или не упрёмся в корень.
    private bool IsInsideContextMenu(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, MiniPlayerContextMenu)) return true;
            if (current is ContextMenu) return false;

            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return false;
    }

    private void CommitOpacityValueEdit()
    {
        if (!_isEditingOpacityValue) return;
        _isEditingOpacityValue = false;

        double sliderValue = MiniOpacityContextSlider.Value;
        string text = MiniOpacityContextValueEditor.Text?.Trim() ?? string.Empty;
        if (text.Length > 0 && int.TryParse(text, out int percent))
        {
            sliderValue = Math.Clamp(percent / 100.0, MiniOpacityContextSlider.Minimum, MiniOpacityContextSlider.Maximum);
            MiniOpacityContextSlider.Value = sliderValue;
        }

        MiniOpacityContextValueText.Text = $"{(int)Math.Round(sliderValue * 100)}%";
        MiniOpacityContextValueEditor.Visibility = Visibility.Collapsed;
        MiniOpacityContextValueText.Visibility = Visibility.Visible;
    }

    private void CancelOpacityValueEdit()
    {
        if (!_isEditingOpacityValue) return;
        _isEditingOpacityValue = false;

        MiniOpacityContextValueText.Text = $"{(int)Math.Round(MiniOpacityContextSlider.Value * 100)}%";
        MiniOpacityContextValueEditor.Visibility = Visibility.Collapsed;
        MiniOpacityContextValueText.Visibility = Visibility.Visible;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mainWindow.Settings.MiniPlayerPinned && e.ButtonState == MouseButtonState.Pressed)
            DragMove();

        // Помечаем событие обработанным независимо от DragMove (при закреплении его нет), иначе оно всплывёт до RootBorder
        // и вызовет RootBorder_MouseLeftButtonDown повторно (см. комментарий у него).
        e.Handled = true;
    }

    // Перетаскивание за любое свободное место (прогресс-бар и кнопки сами ставят Handled). На HeaderPanel не работало:
    // в Overlay-режиме она прячется при наведении; RootBorder от видимости элементов не зависит.
    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mainWindow.Settings.MiniPlayerPinned && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // DragMove блокирует поток до отпускания мыши, поэтому момент сдвига ловим через LocationChanged — оно срабатывает
    // на каждое перемещение, включая итоговую позицию.
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        _mainWindow.SaveMiniPlayerPosition(Left, Top);
    }

    // Режим кнопок (AppSettings.MiniPlayerButtonsLayout): "Below" — ControlsPanel отдельной строкой, окно растёт при наведении;
    // "Overlay" — кнопки на месте HeaderPanel, окно не растёт. Вызывается при открытии и смене настройки (…ButtonsLayoutLive).
    private static readonly Thickness ControlsPanelMarginBelow = new(0, 2, 0, 10);
    // Не readonly: пересчитывается в UpdateControlsPanelOverlayMargin при смене отступа HeaderPanel; здесь — безопасный дефолт.
    private Thickness ControlsPanelMarginOverlay = new(0, 8, 0, 0);

    public void ApplyButtonsLayoutMode()
    {
        _buttonsOverlayMode = _mainWindow.Settings.MiniPlayerButtonsLayout == "Overlay";

        UpdateControlsPanelOverlayMargin();
        Grid.SetRow(ControlsPanel, _buttonsOverlayMode ? 0 : 2);
        ControlsPanel.Margin = _buttonsOverlayMode ? ControlsPanelMarginOverlay : ControlsPanelMarginBelow;

        // Сбрасываем в "курсор снаружи" даже если мышь над окном: следующий RootBorder_MouseEnter/Leave всё поправит, а старт
        // с заведомо согласованного состояния надёжнее, чем угадывать, в каком из двух "развёрнутых" состояний мы были.
        _volumeOverlayRestoreTimer?.Stop();
        _volumeOverlayRestoreTimer = null;
        _volumeOverlaySuppressedControls = false;
        HeaderPanel.Visibility = Visibility.Visible;
        HeaderPanel.IsHitTestVisible = true;
        ControlsPanel.Visibility = Visibility.Collapsed;
        Height = MeasureContentHeight();
    }

    // Показывает/прячет полосу прогресса (AppSettings.MiniPlayerShowProgress); при открытии и при смене настройки на открытом
    // мини-плеере (MainWindow.ApplyMiniPlayerProgressBarVisibilityLive), как ApplyButtonsLayoutMode.
    public void ApplyProgressBarVisibility()
    {
        _showProgress = _mainWindow.Settings.MiniPlayerShowProgress;
        ProgressRow.Visibility = _showProgress ? Visibility.Visible : Visibility.Collapsed;

        // Без полосы прогресса нижний отступ заголовка равен верхнему (10,8,10,10 вместо 10,8,10,2), чтобы вокруг него было
        // поровну места (см. HeaderBottomMarginWithProgress/WithoutProgress).
        HeaderPanel.Margin = new Thickness(HeaderHorizontalMargin, HeaderTopMargin, HeaderHorizontalMargin,
            _showProgress ? HeaderBottomMarginWithProgress : HeaderBottomMarginWithoutProgress);
        UpdateControlsPanelOverlayMargin();

        Height = MeasureContentHeight();
    }

    // В Overlay-режиме ControlsPanel делит Row 0 с HeaderPanel: без компенсации кнопки центрировались бы по своей высоте,
    // а не по занятому месту, и съезжали бы при смене видимости полосы; top − bottom — асимметрия отступов HeaderPanel.
    private void UpdateControlsPanelOverlayMargin()
    {
        double headerBottom = _showProgress ? HeaderBottomMarginWithProgress : HeaderBottomMarginWithoutProgress;
        ControlsPanelMarginOverlay = new Thickness(0, HeaderTopMargin - headerBottom, 0, 0);
        if (_buttonsOverlayMode) ControlsPanel.Margin = ControlsPanelMarginOverlay;
    }

    // Тонкий акцентный контур вокруг обложки не меняет высоту мини-плеера и не получает мышь: перемотка остаётся
    // на существующей горизонтальной полосе.
    public void ApplyArtworkProgressVisibility()
    {
        _showArtworkProgress = _mainWindow.Settings.MiniPlayerShowArtworkProgress;
        var visibility = _showArtworkProgress ? Visibility.Visible : Visibility.Collapsed;
        ArtProgressTrack.Visibility = visibility;
        ArtProgressOutline.Visibility = visibility;
        UpdateArtworkProgressOutline(_lastCurrentSeconds, _lastTotalSeconds);
    }

    // Толщина применяется к фоновому треку и акцентному штриху одновременно; геометрия пересчитывается, чтобы внешняя
    // граница линии осталась на форме обложки без цветных фрагментов в углах.
    public void ApplyArtworkProgressThickness()
    {
        // Контур должен иметь центр линии ровно на границе обложки.
        // Поэтому при толщине 4 px: 2 px находятся внутри, 2 px снаружи.
        double thickness = Math.Clamp(_mainWindow.Settings.MiniPlayerArtworkProgressThickness, 2.0, 4.0);
        const double artworkSize = 42.0;
        const double canvasSize = 50.0;

        ArtworkCanvas.Width = canvasSize;
        ArtworkCanvas.Height = canvasSize;
        ArtProgressTrack.Width = artworkSize;
        ArtProgressTrack.Height = artworkSize;

        bool circle = string.Equals(_mainWindow.Settings.MiniPlayerArtworkStyle, "Vinyl", StringComparison.Ordinal)
                      || string.Equals(_mainWindow.Settings.MiniPlayerArtworkStyle, "StaticCircle", StringComparison.Ordinal);

        ArtProgressTrack.CornerRadius = circle
            ? new CornerRadius(artworkSize / 2.0)
            : new CornerRadius(8.0);
        ArtProgressTrack.BorderThickness = new Thickness(0);
        ArtProgressOutline.StrokeThickness = thickness;

        UpdateArtworkProgressOutline(_lastCurrentSeconds, _lastTotalSeconds);
    }

    // Фиксированный цвет либо используемый акцент оформления; явная замороженная кисть (не только DynamicResource)
    // надёжно обновляет уже созданное WPF Window при смене акцента Wpf.Ui.
    public void ApplyArtworkProgressColor()
    {
        Color color = _mainWindow.GetResolvedAccentColor();
        if (_mainWindow.Settings.MiniPlayerArtworkProgressColorMode == "Fixed")
        {
            try
            {
                color = (Color)ColorConverter.ConvertFromString(
                    _mainWindow.Settings.MiniPlayerArtworkProgressColorHex);
            }
            catch
            {
                // Повреждённое значение из settings.json не должно скрыть индикатор: безопасно
                // откатываемся к текущему акценту оформления.
            }
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        ArtProgressOutline.Stroke = brush;
    }

    private void UpdateArtworkProgressOutline(double currentSeconds, double totalSeconds)
    {
        double ratio = totalSeconds > 0
            ? Math.Clamp(currentSeconds / totalSeconds, 0.0, 1.0)
            : 0.0;
        UpdateArtworkProgressOutline(ratio);
    }

    private void UpdateArtworkProgressOutline(double ratio)
    {
        if (!_showArtworkProgress || ratio <= 0.0001)
        {
            ArtProgressOutline.Data = null;
            ArtProgressOutline.StrokeDashArray = null;
            ArtProgressOutline.StrokeDashOffset = 0;
            return;
        }

        ratio = Math.Clamp(ratio, 0.0, 1.0);

        bool circle = string.Equals(_mainWindow.Settings.MiniPlayerArtworkStyle, "Vinyl", StringComparison.Ordinal)
                      || string.Equals(_mainWindow.Settings.MiniPlayerArtworkStyle, "StaticCircle", StringComparison.Ordinal);

        const double artworkSize = 42.0;
        const double canvasSize = 50.0;
        const double offset = (canvasSize - artworkSize) / 2.0;
        const double center = offset + artworkSize / 2.0;

        if (circle)
        {
            double radius = artworkSize / 2.0;
            ArtProgressOutline.Data = new EllipseGeometry(
                new Point(center, center), radius, radius);

            double circumference = 2.0 * Math.PI * radius;
            ApplyArtworkProgressDash(ratio, circumference, startOffset: 0.0);
            return;
        }

        const double cornerRadius = 8.0;
        double left = offset;
        double top = offset;
        double right = offset + artworkSize;
        double bottom = offset + artworkSize;
        double topCenter = center;

        // Build the rounded-square path explicitly so its first point is the center of the top edge,
        // instead of relying on the internal start point of RectangleGeometry.
        var figure = new PathFigure
        {
            StartPoint = new Point(topCenter, top),
            IsClosed = true,
            IsFilled = false
        };

        figure.Segments.Add(new LineSegment(
            new Point(right - cornerRadius, top), true));
        figure.Segments.Add(new ArcSegment(
            new Point(right, top + cornerRadius),
            new Size(cornerRadius, cornerRadius),
            0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(
            new Point(right, bottom - cornerRadius), true));
        figure.Segments.Add(new ArcSegment(
            new Point(right - cornerRadius, bottom),
            new Size(cornerRadius, cornerRadius),
            0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(
            new Point(left + cornerRadius, bottom), true));
        figure.Segments.Add(new ArcSegment(
            new Point(left, bottom - cornerRadius),
            new Size(cornerRadius, cornerRadius),
            0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(
            new Point(left, top + cornerRadius), true));
        figure.Segments.Add(new ArcSegment(
            new Point(left + cornerRadius, top),
            new Size(cornerRadius, cornerRadius),
            0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(
            new Point(topCenter, top), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ArtProgressOutline.Data = geometry;

        double straight = artworkSize - 2.0 * cornerRadius;
        double perimeter = 4.0 * straight + 2.0 * Math.PI * cornerRadius;

        // The path itself starts at the exact center of the top edge, so no
        // dash offset is needed.
        ApplyArtworkProgressDash(ratio, perimeter, startOffset: 0.0);
    }

    private void ApplyArtworkProgressDash(double ratio, double perimeter, double startOffset)
    {
        if (ratio >= 0.9999)
        {
            ArtProgressOutline.StrokeDashArray = null;
            ArtProgressOutline.StrokeDashOffset = 0;
            return;
        }

        // DashArray is expressed in multiples of StrokeThickness.
        // Keep one continuous dash and one continuous gap.
        double thickness = Math.Max(ArtProgressOutline.StrokeThickness, 0.01);
        double dashLength = Math.Max(perimeter * ratio, 0.001);
        double gapLength = Math.Max(perimeter - dashLength, 0.001);

        ArtProgressOutline.StrokeDashArray = new DoubleCollection
        {
            dashLength / thickness,
            gapLength / thickness
        };

        ArtProgressOutline.StrokeDashOffset = -startOffset / thickness;
    }

    private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Visible;

        if (_buttonsOverlayMode)
        {
            // Hidden, а не Collapsed: Collapsed убирает HeaderPanel из расчёта Auto-строки Row 0, и ControlsPanel прилипал бы
            // к верхнему краю; IsHitTestVisible=false — чтобы невидимая HeaderPanel не перехватывала клики ControlsPanel.
            HeaderPanel.Visibility = Visibility.Hidden;
            HeaderPanel.IsHitTestVisible = false;
        }
        else
        {
            Height = MeasureContentHeight();
        }
    }

    private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Collapsed;

        if (_buttonsOverlayMode)
        {
            HeaderPanel.Visibility = Visibility.Visible;
            HeaderPanel.IsHitTestVisible = true;
        }
        else
        {
            Height = MeasureContentHeight();
        }
    }

    // Прокрутка колесом мыши в любом месте мини-плеера крутит громкость — тот же шаг
    // (5% за деление), что и у хоткеев и у прокрутки над ползунком в главном окне.
    private void RootBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _mainWindow.ExternalChangeVolume(Math.Sign(e.Delta) * 0.02);
        e.Handled = true;
    }

    private void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.CaptureMouse();
        _isDraggingProgress = true;
        SeekFromMouse(e.GetPosition(overlay).X, overlay.ActualWidth);

        // Иначе клик по прогресс-бару всплыл бы дальше до RootBorder_MouseLeftButtonDown и
        // одновременно с перемоткой попытался бы начать перетаскивание окна тем же кликом.
        e.Handled = true;
    }

    private void Progress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress) return;
        var overlay = (FrameworkElement)sender;
        SeekFromMouse(e.GetPosition(overlay).X, overlay.ActualWidth);
    }

    private void Progress_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var overlay = (FrameworkElement)sender;
        overlay.ReleaseMouseCapture();
        _isDraggingProgress = false;
    }

    private void SeekFromMouse(double x, double width)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(x / width, 0.0, 1.0);
        ProgressFill.Width = Math.Max(ActualWidth - 20, 0) * ratio;
        UpdateArtworkProgressOutline(ratio);
        _mainWindow.ExternalSeekRatio(ratio);
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _topmostTimer.Tick -= TopmostTimer_Tick;
        StopVinylRotation();
        _volumeOverlayRestoreTimer?.Stop();
        if (_volumeOverlayRestoreTimer is not null)
            _volumeOverlayRestoreTimer.Tick -= VolumeOverlayRestoreTimer_Tick;
        _volumeOverlayRestoreTimer = null;

        _mainWindow.TrackInfoChanged -= OnTrackInfoChanged;
        _mainWindow.PlaybackState.Changed -= OnPlaybackSnapshotChanged;
        _mainWindow.VolumeChanged -= OnVolumeChanged;
        _mainWindow.RepeatModeChanged -= OnRepeatModeChanged;
        _mainWindow.ShuffleStateChanged -= OnShuffleStateChanged;
        FavoritesChangeNotifier.Instance.PropertyChanged -= OnFavoritesChanged;
        base.OnClosed(e);
    }
}
