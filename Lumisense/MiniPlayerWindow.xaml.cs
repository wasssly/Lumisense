using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace AudioPlayer;

public partial class MiniPlayerWindow : Window
{
    private readonly MainWindow _mainWindow;
    private bool _isDraggingProgress;

    // Нижний отступ HeaderPanel в XAML — "14,10,14,2" (верхний 10, нижний 2): при видимой
    // полосе прогресса это осознанная асимметрия, утягивающая заголовок к бару под ним. Без
    // полосы это выглядит неровно, поэтому нижний отступ увеличивается до 10 при её скрытии —
    // см. ApplyProgressBarVisibility.
    private const double HeaderBottomMarginWithProgress = 2;
    private const double HeaderBottomMarginWithoutProgress = 10;

    // Из настроек (см. ApplyProgressBarVisibility) — показывать полосу прогресса сейчас или нет.
    private bool _showProgress = true;

    // Реальный замер (Measure), а не заранее подобранные константы под каждую комбинацию
    // видимости строк — та комбинация слишком легко расходится с реальной раскладкой (Grid с
    // рядами Auto отдаёт лишнее/недостающее место последнему ряду, а не распределяет поровну).
    private double MeasureContentHeight()
    {
        ContentGrid.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        return ContentGrid.DesiredSize.Height;
    }

    // ---------- Прилипание к краям экрана ----------
    // Сама механика (перехват WM_MOVING, арифметика прилипания) — в WindowSnapHelper, общем
    // для этого окна и MainWindow. Включение/выключение — AppSettings.MiniPlayerSnapToEdges
    // (см. страницу "Мини-плеер" в настройках), по умолчанию включено — прежнее поведение до
    // появления этой настройки.

    private static readonly IntPtr HWND_TOPMOST = WindowSnapHelper.HWND_TOPMOST;
    private const uint SWP_NOMOVE = WindowSnapHelper.SWP_NOMOVE;
    private const uint SWP_NOSIZE = WindowSnapHelper.SWP_NOSIZE;
    private const uint SWP_NOACTIVATE = WindowSnapHelper.SWP_NOACTIVATE;

    // Windows иногда молча теряет топмост-состояние окна (флаг формально остаётся, а
    // реальный Z-order — нет) — после полноэкранных игр, диалогов UAC, RDP, блокировки экрана
    // и т.п. Раз в несколько секунд принудительно переустанавливаем окно поверх остальных
    // через Win32 SetWindowPos — это чинит уже "отвалившийся" топмост, а не только поддерживает.
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private IntPtr _hwnd;

    // Снимок состояния на момент начала текущего перетаскивания — позиция курсора и
    // прямоугольник окна. Все расчёты внутри одного перетаскивания идут от этого снимка,
    // а не от прямоугольника из предыдущего WM_MOVING — иначе окно, прижавшееся к краю,
    // почти не удавалось оттащить обратно: каждое новое сообщение отталкивалось уже от
    // прижатой позиции. Так позиция всегда — чистое смещение курсора от точки старта.
    private bool _isDragging;
    private WindowSnapHelper.POINT _dragStartCursor;
    private WindowSnapHelper.RECT _dragStartRect;

    // См. ApplyButtonsLayoutMode — true, когда в настройках выбран режим "кнопки на месте
    // обложки" (AppSettings.MiniPlayerButtonsLayout == "Overlay") вместо прежнего "снизу".
    private bool _buttonsOverlayMode;
    private DispatcherTimer? _volumeOverlayRestoreTimer;
    private bool _volumeOverlaySuppressedControls;

    public MiniPlayerWindow(MainWindow mainWindow)
    {
        InitializeComponent();

        _mainWindow = mainWindow;
        _mainWindow.TrackInfoChanged += OnTrackInfoChanged;
        _mainWindow.ProgressChanged += OnProgressChanged;
        _mainWindow.PlaybackStateChanged += OnPlaybackStateChanged;
        _mainWindow.VolumeChanged += OnVolumeChanged;
        _mainWindow.RepeatModeChanged += OnRepeatModeChanged;
        _mainWindow.ShuffleStateChanged += OnShuffleStateChanged;
        FavoritesChangeNotifier.Instance.PropertyChanged += OnFavoritesChanged;

        ApplyButtonsLayoutMode();
        ApplyProgressBarVisibility();

        // Сразу отображаем текущее состояние плеера
        OnTrackInfoChanged(_mainWindow.CurrentTitle, _mainWindow.CurrentArtist, _mainWindow.CurrentArtBrush);
        OnPlaybackStateChanged(_mainWindow.IsPlayingNow);
        UpdateSecondaryButton();

        // Название могло быть длинным ещё до открытия мини-плеера — пересчитываем бегущую
        // строку после первого прохода layout, когда TitleClipBorder.ActualWidth уже известен.
        Loaded += (_, _) => UpdateTitleMarquee();

        _topmostTimer.Tick += TopmostTimer_Tick;
        _topmostTimer.Start();
    }

    // См. комментарий у объявления _topmostTimer — периодически принудительно возвращаем
    // окно в топмост через Win32, а не полагаемся на то, что WPF Topmost=true держится сам.
    // Трогаем реальный Z-order только когда настройка "поверх окон" включена и мини-плеер
    // не свёрнут — незачем дёргать SetWindowPos впустую.
    private void TopmostTimer_Tick(object? sender, EventArgs e)
    {
        if (!Topmost || _hwnd == IntPtr.Zero || WindowState == WindowState.Minimized) return;

        WindowSnapHelper.SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Перехватываем оконные сообщения на уровне Win32: это единственный способ подправить
    // позицию окна прямо во время родного интерактивного перетаскивания (DragMove), не дожидаясь
    // его завершения — за счёт этого прилипание к краю ощущается плавным и "магнитным", а не
    // рывком после отпускания мыши.
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
                // Начало нового перетаскивания — фиксируем точку отсчёта. GetWindowRect
                // отдаёт физические пиксели — те же единицы, что и GetCursorPos и WM_MOVING,
                // так что на мониторах с масштабированием (100% ≠ 125%/150% и т.д.) расчёт
                // остаётся точным.
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

    // Сырые значения с последнего OnTrackInfoChanged/OnProgressChanged — нужны, чтобы
    // UpdateSecondaryLine могла перерисовать вторую строку по актуальным данным в любой
    // момент, а не только когда придёт следующее событие (например, сразу после того как
    // пользователь переключил AppSettings.MiniPlayerInfoMode в настройках, см. ApplyInfoModeLive).
    private string _lastArtist = "";
    private double _lastCurrentSeconds;
    private double _lastTotalSeconds;

    private void OnTrackInfoChanged(string title, string artist, Brush? art)
    {
        TitleText.Text = title;
        _lastArtist = artist;
        UpdateSecondaryLine();

        // Новый трек — новое избранное-состояние; если сейчас выбран режим "Избранное" (см.
        // SecondaryButtonMode), сердечко должно тут же отразить статус НОВОГО трека, а не
        // донашивать вид предыдущего до следующего клика по нему где-либо ещё.
        if (SecondaryButtonMode == "Favorite") UpdateFavoriteSecondaryButtonVisual();

        if (art != null)
        {
            ArtBorder.Background = art;
            ArtIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtBorder.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
            ArtIcon.Visibility = Visibility.Visible;
        }

        UpdateTitleMarquee();
    }

    // Вторая строка заголовка (под названием трека, которое видно всегда независимо от
    // режима) — что именно в ней показывать, выбирается в настройках (см.
    // AppSettings.MiniPlayerInfoMode и страницу настроек "Мини-плеер"). Вызывается и на
    // каждое обновление трека/прогресса, и сразу же при переключении самой настройки, пока
    // мини-плеер уже открыт (см. ApplyInfoModeLive) — по той же схеме, что и
    // UpdateSecondaryButton/ApplyButtonsLayoutMode для остального содержимого мини-плеера.
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

    // ---------- Фон и тема мини-плеера ----------
    //
    // От Win32-блюра (SetWindowCompositionAttribute/ACCENT_ENABLE_ACRYLICBLURBEHIND) отказались:
    // конфликтовал с тем, что окно уже само AllowsTransparency="True" (layered window со своим
    // альфа-смешиванием), на части систем ползунок прозрачности переставал на что-либо влиять.
    //
    // Сейчас всё на одном слое: RootBorder заливается сплошным SolidColorBrush
    // (MiniBackgroundBrush), альфа-канал которого и есть настройка "прозрачность мини-плеера" —
    // WPF сам честно смешивает его с тем, что позади окна. Никакого Win32, никакой зависимости
    // от DWM. Базовый RGB-цвет фона и цвета текста/иконок зависят от темы приложения (ApplyTheme).

    // Базовые RGB для фона (альфа добавляется отдельно в ApplyBackground). Светлая тема —
    // светло-серый, а не чистый белый: на полупрозрачном белом поверх произвольного рабочего
    // стола тёмный текст читается плохо без хоть какой-то плотности цвета.
    private static readonly (byte R, byte G, byte B) DarkBackgroundRgb = (0x1C, 0x1C, 0x1E);
    private static readonly (byte R, byte G, byte B) LightBackgroundRgb = (0xF2, 0xF2, 0xF2);

    private bool _isLightTheme;

    // Кисти получаются через FindResource по x:Key и кэшируются один раз, чтобы не искать
    // их в дереве ресурсов при каждом обновлении темы/прозрачности
    private SolidColorBrush? _textPrimaryBrush;
    private SolidColorBrush? _textSecondaryBrush;
    private SolidColorBrush? _controlFillBrush;
    private SolidColorBrush? _controlFillSecondaryBrush;
    private SolidColorBrush? _controlStrongFillBrush;
    private SolidColorBrush? _controlStrokeBrush;

    // Пересчитывает все цвета, зависящие от темы приложения (фон, текст, иконки, подложки
    // кнопок) — вызывается один раз при открытии мини-плеера (см. OnSourceInitialized) и затем
    // повторно, если пользователь переключил тему в настройках, пока мини-плеер уже открыт
    // (см. ApplyThemeLive / MainWindow.ApplyMiniPlayerThemeLive).
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
        byte alpha = (byte)Math.Round(Math.Clamp(_mainWindow.Settings.MiniPlayerOpacity, 0.0, 1.0) * 255);
        var rgb = _isLightTheme ? LightBackgroundRgb : DarkBackgroundRgb;
        MiniBackgroundBrush.Color = Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }

    // Вызывается из MainWindow.ApplyMiniPlayerOpacityLive, когда пользователь двигает
    // слайдер прозрачности в окне настроек, пока мини-плеер уже открыт.
    public void ApplyOpacityLive() => ApplyBackground();

    // Вызывается из MainWindow.ApplyMiniPlayerThemeLive, когда пользователь переключает
    // светлую/тёмную тему в настройках, пока мини-плеер уже открыт.
    public void ApplyThemeLive() => ApplyTheme();

    // ---------- Бегущая строка названия трека ----------
    // Название показывается статично, пока помещается в 140px. Если длиннее — бесконечная
    // анимация TranslateTransform.X: пауза → проезд до конца → пауза → проезд обратно, по кругу,
    // с постоянной скоростью (px/сек), а не фиксированным временем на весь текст.
    //
    // Ширина меряется через собственный Measure() у TitleText, а не ActualWidth (доступен
    // только после layout). Раньше считалась через отдельный FormattedText с тем же Typeface,
    // но тот разрешает шрифт (переменные шрифты вроде Segoe UI Variable) чуть иначе, чем рисует
    // TextBlock — дистанция прокрутки получалась короче настоящей, и строка останавливалась, не
    // докрутив текст. MarqueeEndBufferPx — запас на случай, если засечки/антиалиасинг выходят
    // за расчётную ширину.
    private const double MarqueePixelsPerSecond = 34.0;
    private const double MarqueeEdgePauseSeconds = 1.0;
    private const double DefaultTitleClipWidth = 140.0;
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

    // ---------- Всплывающий индикатор процентов громкости ----------
    //
    // Показывается при любом изменении громкости, пока открыт мини-плеер — то есть как раз
    // при регулировке хоткеями или скроллом (у мини-плеера нет собственного ползунка). Каждый
    // вызов останавливает предыдущий прогон Storyboard и запускает новый с нуля, поэтому
    // быстрые повторные нажатия хоткея просто продлевают показ, а не мигают.
    private void OnVolumeChanged(double volume)
    {
        VolumeIndicatorText.Text = $"{(int)Math.Round(volume * 100)}%";
        VolumeIndicatorIcon.Icon = volume <= 0.0 ? "IconSpeakerMute" : "IconSpeaker";

        if (_buttonsOverlayMode)
        {
            // В Overlay-режиме при наведении ControlsPanel занимает ту же строку, что и
            // HeaderPanel. На время volume indicator убираем кнопки, иначе процент громкости
            // отображается одновременно с ними и элементы перекрываются.
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

        if (!_volumeOverlaySuppressedControls || !_buttonsOverlayMode) return;
        _volumeOverlaySuppressedControls = false;

        if (RootBorder.IsMouseOver)
        {
            ControlsPanel.Visibility = Visibility.Visible;
            HeaderPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProgressChanged(double currentSeconds, double totalSeconds)
    {
        _lastCurrentSeconds = currentSeconds;
        _lastTotalSeconds = totalSeconds;
        if (_mainWindow.Settings.MiniPlayerInfoMode == "TitleRemaining") UpdateSecondaryLine();

        if (_isDraggingProgress || totalSeconds <= 0) return;

        double ratio = Math.Clamp(currentSeconds / totalSeconds, 0.0, 1.0);
        double trackWidth = Math.Max(ActualWidth - 28, 0); // 28 = отступы слева/справа (14+14)
        ProgressFill.Width = trackWidth * ratio;
    }

    private void OnPlaybackStateChanged(bool isPlaying)
    {
        PlayPauseButton.Icon = IconResources.MakeOnAccent(isPlaying ? "IconPause" : "IconPlay");
        PlayPauseButton.Background = new SolidColorBrush(_mainWindow.GetResolvedAccentColor()); // всегда акцентная
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalNext();
    private void PrevButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalPrev();
    private void RestoreButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExitMiniMode();
    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _mainWindow.ShowSettingsWindow("MiniPlayer");

    // Не полагаемся на ControlAppearance.Primary у WPF-UI для "включённого" вида этих кнопок —
    // тот же подтверждённый баг библиотеки, что и в MainWindow.SetAccentButtonActive (фон не
    // обновляется вживую при смене акцента). Красим Background вручную тем же способом.
    private void SetAccentButtonActive(Wpf.Ui.Controls.Button button, bool active)
    {
        button.Appearance = ControlAppearance.Secondary;

        if (active)
            button.Background = new SolidColorBrush(_mainWindow.GetResolvedAccentColor());
        else
            button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
    }

    // Компактное окно мини-плеера — под кнопку повтора, кнопку "перемешать" и сердечко
    // избранного одновременно места нет (в отличие от основного окна, где показаны все три),
    // поэтому здесь всего одна "вторая" кнопка, а какую из трёх функций она выполняет,
    // выбирается в настройках (см. AppSettings.MiniPlayerSecondaryButton и SettingsWindow,
    // страница "Мини-плеер"). SecondaryButton в разметке — один и тот же элемент под все три
    // функции, никогда не больше одной сразу.
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

    // Синхронизирует вид кнопки повтора с фактическим режимом в основном окне — тот же набор
    // иконок/акцента, что и у RepeatButton там (см. MainWindow.SetRepeatMode), просто в
    // уменьшенном размере под мини-плеер. Применяется только если сейчас выбрана функция
    // "Повтор" (см. SecondaryButtonMode) — иначе кнопка сейчас показывает что-то другое, и
    // трогать её вид отсюда не нужно (когда пользователь переключит настройку обратно,
    // UpdateSecondaryButton сама подставит актуальный режим повтора).
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

    // Третий вариант "второй кнопки" — избранное текущего трека. В отличие от повтора и
    // перемешивания, у избранного нет отдельного события на MainWindow: FavoritesManager
    // глобальный и статический, а на его изменения подписан FavoritesChangeNotifier.Instance
    // (см. Favorites.cs) — тот же Epoch-приём, на котором держится сердечко в обычном
    // плейлисте (IsFavoriteMultiConverter). Подписка идёт в конструкторе безусловно — сама
    // проверка режима внутри дешевле, чем подписываться/отписываться при каждом переключении
    // настройки.
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

    // Вызывается при открытии мини-плеера (см. конструктор) и сразу же, если пользователь
    // переключил настройку "какую функцию показывать" в окне настроек прямо сейчас, пока
    // мини-плеер открыт (см. MainWindow.ApplyMiniPlayerSecondaryButtonLive) — перерисовывает
    // SecondaryButton под актуально выбранную функцию, используя уже известное из основного
    // окна текущее состояние (так же, как конструктор поступает с play/pause при открытии).
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

    // Вызывается из MainWindow.RefreshAccentDependentIcons при каждой смене акцента — сама
    // by себе смена состояния (повтор/шафл вкл-выкл, играет/на паузе) уже красит кнопки через
    // SetAccentButtonActive/OnPlaybackStateChanged выше, а тут нужно перекрасить их и тогда,
    // когда состояние НЕ менялось, а сменился только сам цвет акцента.
    public void RefreshAccentButtons()
    {
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

    // Подставляем актуальное состояние настроек прямо перед показом меню — на случай, если
    // закрепление/топмост поменяли в другом месте (например, в окне настроек) уже после
    // того, как это меню было создано.
    // Пока true — MiniOpacityContextSlider.Value выставляется программно (см.
    // MiniPlayerContextMenu_Opened), и ValueChanged должен промолчать, а не воспринять это как
    // движение слайдера пользователем и не запустить повторное, уже ненужное применение
    // настройки (и тем более не уйти в цикл обновлений с окном настроек).
    private bool _isSyncingOpacitySlider;

    // Пока true — идёт перетаскивание прозрачным Border'ом поверх MiniOpacityContextSlider (см.
    // MiniOpacityContextOverlay_MouseLeftButtonDown/Up ниже) — тот же приём, что и у
    // _isDraggingOpacityOverlay в SettingsWindow.xaml.cs.
    private bool _isDraggingOpacityOverlay;

    private void MiniPlayerContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        PinnedMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerPinned;
        TopmostMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerAlwaysOnTop;

        _isSyncingOpacitySlider = true;
        MiniOpacityContextSlider.Value = _mainWindow.Settings.MiniPlayerOpacity;
        MiniOpacityContextValueText.Text = $"{(int)Math.Round(_mainWindow.Settings.MiniPlayerOpacity * 100)}%";
        _isSyncingOpacitySlider = false;
    }

    private void PinnedMenuItem_Click(object sender, RoutedEventArgs e)
        => _mainWindow.SetMiniPlayerPinned(PinnedMenuItem.IsChecked);

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
        => _mainWindow.SetMiniPlayerTopmost(TopmostMenuItem.IsChecked);

    // Оверлей поверх MiniOpacityContextSlider (см. MiniPlayerWindow.xaml) — сам Slider
    // IsHitTestVisible="False", мышь ловит этот прозрачный Border и сам вычисляет значение по
    // X-координате клика/перетаскивания. Тот же приём, что и у MiniOpacitySlider в
    // SettingsWindow.xaml (см. UpdateSliderValueFromMouse там) — здесь он нужен даже больше:
    // Slider живёт внутри ContextMenu (отдельный Popup), где нативный захват мыши самим
    // Thumb'ом при разворачивании из MenuItem ведёт себя нестабильно, а явный
    // Border.CaptureMouse() от этого не зависит.
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

    private void UpdateOpacitySliderFromMouse(double positionX, double width)
    {
        if (width <= 0) return;

        double ratio = Math.Clamp(positionX / width, 0.0, 1.0);
        MiniOpacityContextSlider.Value = MiniOpacityContextSlider.Minimum
            + ratio * (MiniOpacityContextSlider.Maximum - MiniOpacityContextSlider.Minimum);
    }

    private void MiniOpacityContextSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Может выстрелить ещё ВНУТРИ InitializeComponent(), до "_mainWindow = mainWindow;" в
        // конструкторе: RangeBase.OnMinimumChanged коэрсит Value и синхронно поднимает
        // ValueChanged прямо во время разбора XAML, когда поля вроде _mainWindow ещё не
        // готовы. Value="1.0" в XAML снимает саму причину (уже больше Minimum="0.3"), проверка
        // ниже — страховка. _mainWindow, а не флаг _isSyncingOpacitySlider — он не-null строго
        // после InitializeComponent(), то есть надёжно гвардит именно этот момент.
        if (_mainWindow == null) return;
        if (_isSyncingOpacitySlider) return;

        MiniOpacityContextValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        _mainWindow.SetMiniPlayerOpacity(e.NewValue);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mainWindow.Settings.MiniPlayerPinned && e.ButtonState == MouseButtonState.Pressed)
            DragMove();

        // Помечаем событие обработанным независимо от того, сработал ли DragMove выше
        // (не сработает, если закреплено) — иначе оно продолжит всплывать до RootBorder и
        // вызовет RootBorder_MouseLeftButtonDown ещё раз поверх уже обработанного клика (см.
        // комментарий у RootBorder_MouseLeftButtonDown ниже о том, зачем вообще нужен этот
        // обработчик там, а не только здесь).
        e.Handled = true;
    }

    // Общий обработчик перетаскивания — ловит клик в любом свободном месте окна, не
    // перехваченном отдельным элементом (прогресс-бар, кнопки — они сами ставят
    // e.Handled = true). Раньше висел только на HeaderPanel, но в режиме
    // AppSettings.MiniPlayerButtonsLayout == "Overlay" при наведении HeaderPanel прячется
    // (см. ApplyButtonsLayoutMode) и её место занимает ControlsPanel — окно переставало
    // перетаскиваться ровно тогда, когда наводишь курсор, чтобы увидеть кнопки. Обработчик на
    // RootBorder не завязан на то, какие элементы сейчас видимы.
    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mainWindow.Settings.MiniPlayerPinned && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // DragMove блокирует поток до отпускания кнопки мыши, поэтому момент, когда окно
    // реально сдвинулось с места, проще всего поймать через LocationChanged — оно
    // срабатывает на каждое перемещение, включая последнее (итоговую позицию).
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        _mainWindow.SaveMiniPlayerPosition(Left, Top);
    }

    // Применяет выбранный в настройках режим расположения кнопок управления (см.
    // AppSettings.MiniPlayerButtonsLayout). Вызывается при открытии мини-плеера и повторно,
    // если пользователь переключил настройку прямо сейчас, пока мини-плеер открыт (см.
    // MainWindow.ApplyMiniPlayerButtonsLayoutLive) — по той же схеме, что и
    // UpdateSecondaryButton для кнопки повтора/шафла.
    //
    // "Below" (по умолчанию): ControlsPanel — отдельная строка под прогресс-баром, окно
    // подрастает при наведении (CollapsedHeight → ExpandedHeight). "Overlay": ControlsPanel
    // переносится в ту же строку, что и HeaderPanel — при наведении HeaderPanel прячется и её
    // место занимают кнопки, без роста окна. В режиме Below верхний отступ уменьшен,
    // чтобы кнопки были ближе к информации о треке.
    private static readonly Thickness ControlsPanelMarginBelow = new(0, 2, 0, 10);
    private static readonly Thickness ControlsPanelMarginOverlay = new(0, 12, 0, 6);

    public void ApplyButtonsLayoutMode()
    {
        _buttonsOverlayMode = _mainWindow.Settings.MiniPlayerButtonsLayout == "Overlay";

        Grid.SetRow(ControlsPanel, _buttonsOverlayMode ? 0 : 2);
        ControlsPanel.Margin = _buttonsOverlayMode ? ControlsPanelMarginOverlay : ControlsPanelMarginBelow;

        // Сбрасываем в состояние "курсор снаружи" — даже если мышь на самом деле сейчас
        // висит над окном (маловероятно ровно в момент переключения настройки, но не
        // невозможно): следующий RootBorder_MouseEnter/Leave сам всё поправит, а начинать
        // с заведомо согласованного состояния (обложка видна, кнопки скрыты, окно свёрнуто)
        // надёжнее, чем пытаться угадать, в каком из двух РАЗНЫХ по смыслу "развёрнутых"
        // состояний старого и нового режима мы сейчас находимся.
        _volumeOverlayRestoreTimer?.Stop();
        _volumeOverlayRestoreTimer = null;
        _volumeOverlaySuppressedControls = false;
        HeaderPanel.Visibility = Visibility.Visible;
        ControlsPanel.Visibility = Visibility.Collapsed;
        Height = MeasureContentHeight();
    }

    // Показывает/прячет полосу прогресса (см. AppSettings.MiniPlayerShowProgress, страница
    // "Мини-плеер" в настройках) — вызывается при открытии мини-плеера и повторно, если
    // пользователь переключил настройку прямо сейчас, пока мини-плеер открыт (см.
    // MainWindow.ApplyMiniPlayerProgressBarVisibilityLive), по той же схеме, что и
    // ApplyButtonsLayoutMode выше.
    public void ApplyProgressBarVisibility()
    {
        _showProgress = _mainWindow.Settings.MiniPlayerShowProgress;
        ProgressRow.Visibility = _showProgress ? Visibility.Visible : Visibility.Collapsed;

        // См. комментарий у HeaderBottomMarginWithProgress/WithoutProgress выше — без полосы
        // прогресса под заголовком увеличиваем его нижний отступ до того же значения, что и
        // верхний (14,10,14,10 вместо 14,10,14,2), чтобы вокруг заголовка стало поровну места,
        // а не заметно больше сверху, чем снизу.
        HeaderPanel.Margin = new Thickness(14, 10, 14,
            _showProgress ? HeaderBottomMarginWithProgress : HeaderBottomMarginWithoutProgress);

        Height = MeasureContentHeight();
    }

    private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Visible;

        if (_buttonsOverlayMode)
            HeaderPanel.Visibility = Visibility.Collapsed;
        else
            Height = MeasureContentHeight();
    }

    private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Collapsed;

        if (_buttonsOverlayMode)
            HeaderPanel.Visibility = Visibility.Visible;
        else
            Height = MeasureContentHeight();
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
        ProgressFill.Width = Math.Max(ActualWidth - 28, 0) * ratio;
        _mainWindow.ExternalSeekRatio(ratio);
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _topmostTimer.Tick -= TopmostTimer_Tick;
        _volumeOverlayRestoreTimer?.Stop();
        if (_volumeOverlayRestoreTimer is not null)
            _volumeOverlayRestoreTimer.Tick -= VolumeOverlayRestoreTimer_Tick;
        _volumeOverlayRestoreTimer = null;

        _mainWindow.TrackInfoChanged -= OnTrackInfoChanged;
        _mainWindow.ProgressChanged -= OnProgressChanged;
        _mainWindow.PlaybackStateChanged -= OnPlaybackStateChanged;
        _mainWindow.VolumeChanged -= OnVolumeChanged;
        _mainWindow.RepeatModeChanged -= OnRepeatModeChanged;
        _mainWindow.ShuffleStateChanged -= OnShuffleStateChanged;
        FavoritesChangeNotifier.Instance.PropertyChanged -= OnFavoritesChanged;
        base.OnClosed(e);
    }
}
