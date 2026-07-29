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

    private const double CollapsedHeight = 82;
    private const double ExpandedHeight = 140;

    // ---------- Прилипание к краям экрана ----------
    // Дистанция в физических пикселях, на которой окно "магнитится" к краю рабочей области
    // монитора (без учёта панели задач). Работает независимо по X и Y — поэтому мини-плеер
    // так же аккуратно прилипает и в углы экрана. Значение специально небольшое, чтобы
    // притяжение ощущалось мягким, а не резким "прыжком" окна к краю.
    private const int SnapMarginPx = 10;

    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_MOVING = 0x0216;
    private const int WM_EXITSIZEMOVE = 0x0232;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

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
    private POINT _dragStartCursor;
    private RECT _dragStartRect;

    // См. ApplyButtonsLayoutMode — true, когда в настройках выбран режим "кнопки на месте
    // обложки" (AppSettings.MiniPlayerButtonsLayout == "Overlay") вместо прежнего "снизу".
    private bool _buttonsOverlayMode;

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

        Height = CollapsedHeight;
        ApplyButtonsLayoutMode();

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

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
            case WM_ENTERSIZEMOVE:
                // Начало нового перетаскивания — фиксируем точку отсчёта. GetWindowRect
                // отдаёт физические пиксели — те же единицы, что и GetCursorPos и WM_MOVING,
                // так что на мониторах с масштабированием (100% ≠ 125%/150% и т.д.) расчёт
                // остаётся точным.
                _isDragging = true;
                GetCursorPos(out _dragStartCursor);
                GetWindowRect(_hwnd, out _dragStartRect);
                break;

            case WM_MOVING when !_mainWindow.Settings.MiniPlayerPinned && _isDragging:
                {
                    GetCursorPos(out var cursor);
                    int dx = cursor.X - _dragStartCursor.X;
                    int dy = cursor.Y - _dragStartCursor.Y;

                    var width = _dragStartRect.Right - _dragStartRect.Left;
                    var height = _dragStartRect.Bottom - _dragStartRect.Top;

                    var rect = new RECT
                    {
                        Left = _dragStartRect.Left + dx,
                        Top = _dragStartRect.Top + dy,
                    };
                    rect.Right = rect.Left + width;
                    rect.Bottom = rect.Top + height;

                    SnapToScreenEdges(ref rect);

                    Marshal.StructureToPtr(rect, lParam, false);
                    handled = true;
                    return new IntPtr(1); // приложение обязано вернуть TRUE, если само обработало WM_MOVING
                }

            case WM_EXITSIZEMOVE:
                _isDragging = false;
                break;
        }

        return IntPtr.Zero;
    }

    // Подправляет предложенный Windows прямоугольник окна: если он оказался в пределах
    // SnapMarginPx от какого-либо края рабочей области текущего монитора — ровно к этому краю
    // и прижимаем. Проверяется независимо по горизонтали и вертикали.
    private static void SnapToScreenEdges(ref RECT rect)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        var winBounds = new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
        var workArea = System.Windows.Forms.Screen.FromRectangle(winBounds).WorkingArea;

        if (Math.Abs(rect.Left - workArea.Left) <= SnapMarginPx)
        {
            rect.Left = workArea.Left;
            rect.Right = rect.Left + width;
        }
        else if (Math.Abs(rect.Right - workArea.Right) <= SnapMarginPx)
        {
            rect.Right = workArea.Right;
            rect.Left = rect.Right - width;
        }

        if (Math.Abs(rect.Top - workArea.Top) <= SnapMarginPx)
        {
            rect.Top = workArea.Top;
            rect.Bottom = rect.Top + height;
        }
        else if (Math.Abs(rect.Bottom - workArea.Bottom) <= SnapMarginPx)
        {
            rect.Bottom = workArea.Bottom;
            rect.Top = rect.Bottom - height;
        }
    }

    private void OnTrackInfoChanged(string title, string artist, Brush? art)
    {
        TitleText.Text = title;
        ArtistText.Text = artist;

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

        _isLightTheme = _mainWindow.Settings.Theme == "Light";

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
    //
    // Название показывается статично, пока помещается в отведённые 140px. Если оно длиннее —
    // запускаем бесконечную анимацию TranslateTransform.X: пауза в начале (успеть прочитать
    // начало) → плавный проезд до конца строки → пауза в конце → проезд обратно → снова пауза,
    // и по кругу. Скорость (px/сек) одинаковая для любых названий — едет не "название целиком
    // за секунду", а с постоянной скоростью, поэтому длинные названия просто едут дольше.
    //
    // Ширину текста меряем через собственный Measure() у TitleText, а не ActualWidth (доступен
    // только после layout, а обновлять нужно сразу при смене трека, ещё до Loaded).
    //
    // Раньше ширина считалась через отдельный FormattedText с тем же Typeface — но
    // FormattedText разрешает шрифт (включая переменные шрифты вроде Segoe UI Variable) чуть
    // иначе, чем реально рисует TextBlock, из-за чего дистанция прокрутки получалась короче
    // настоящей и бегущая строка останавливалась, не докрутив последние пиксели текста.
    // Measure() того же самого TitleText, который потом рисуется, эту рассинхронизацию
    // исключает. MarqueeEndBufferPx — небольшой запас на случай, если засечки/курсив/
    // антиалиасинг визуально выходят за расчётную ширину.
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

        var storyboard = (Storyboard)FindResource("VolumeIndicatorStoryboard");
        storyboard.Begin(this, true);
    }

    private void OnProgressChanged(double currentSeconds, double totalSeconds)
    {
        if (_isDraggingProgress || totalSeconds <= 0) return;

        double ratio = Math.Clamp(currentSeconds / totalSeconds, 0.0, 1.0);
        double trackWidth = Math.Max(ActualWidth - 28, 0); // 28 = отступы слева/справа (14+14)
        ProgressFill.Width = trackWidth * ratio;
    }

    private void OnPlaybackStateChanged(bool isPlaying)
    {
        PlayPauseButton.Icon = IconResources.MakeOnAccent(isPlaying ? "IconPause" : "IconPlay");
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalNext();
    private void PrevButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExternalPrev();
    private void RestoreButton_Click(object sender, RoutedEventArgs e) => _mainWindow.ExitMiniMode();
    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _mainWindow.ShowSettingsWindow();

    // Компактное окно мини-плеера — под кнопку повтора и кнопку "перемешать" одновременно
    // места нет (в отличие от основного окна, где показаны обе), поэтому здесь всего одна
    // "вторая" кнопка, а какую из двух функций она выполняет, выбирается в настройках (см.
    // AppSettings.MiniPlayerSecondaryButton и SettingsWindow, страница "Мини-плеер").
    // SecondaryButton в разметке — один и тот же элемент под обе функции, она либо повтор,
    // либо шафл, никогда не обе сразу.
    private bool ShowsShuffleButton => _mainWindow.Settings.MiniPlayerSecondaryButton == "Shuffle";

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShowsShuffleButton)
            _mainWindow.ExternalToggleShuffle();
        else
            _mainWindow.ExternalToggleRepeat();
    }

    // Синхронизирует вид кнопки повтора с фактическим режимом в основном окне — тот же набор
    // иконок/акцента, что и у RepeatButton там (см. MainWindow.SetRepeatMode), просто в
    // уменьшенном размере под мини-плеер. Применяется только если сейчас выбрана функция
    // "Повтор" (см. ShowsShuffleButton) — иначе кнопка сейчас показывает шафл, и трогать её
    // вид отсюда не нужно (когда пользователь переключит настройку обратно, UpdateSecondaryButton
    // сама подставит актуальный режим повтора).
    private void OnRepeatModeChanged(string modeName)
    {
        if (ShowsShuffleButton) return;

        switch (modeName)
        {
            case "All":
                SecondaryButton.Icon = IconResources.MakeOnAccent("IconRepeatAll", size: 12);
                SecondaryButton.Appearance = ControlAppearance.Primary;
                break;
            case "One":
                SecondaryButton.Icon = IconResources.MakeOnAccent("IconRepeatOne", size: 12);
                SecondaryButton.Appearance = ControlAppearance.Primary;
                break;
            default:
                SecondaryButton.Icon = IconResources.Make("IconRepeatAll", size: 12);
                SecondaryButton.Appearance = ControlAppearance.Secondary;
                break;
        }
    }

    // Зеркальный аналог OnRepeatModeChanged для перемешивания — применяется, только если
    // сейчас выбрана функция "Перемешать" (см. ShowsShuffleButton), по той же причине.
    private void OnShuffleStateChanged(bool enabled)
    {
        if (!ShowsShuffleButton) return;

        SecondaryButton.Icon = enabled
            ? IconResources.MakeOnAccent("IconShuffle", size: 12)
            : IconResources.Make("IconShuffle", size: 12);
        SecondaryButton.Appearance = enabled ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    // Вызывается при открытии мини-плеера (см. конструктор) и сразу же, если пользователь
    // переключил настройку "какую функцию показывать" в окне настроек прямо сейчас, пока
    // мини-плеер открыт (см. MainWindow.ApplyMiniPlayerSecondaryButtonLive) — перерисовывает
    // SecondaryButton под актуально выбранную функцию, используя уже известное из основного
    // окна текущее состояние (так же, как конструктор поступает с play/pause при открытии).
    public void UpdateSecondaryButton()
    {
        if (ShowsShuffleButton)
            OnShuffleStateChanged(_mainWindow.CurrentIsShuffleEnabled);
        else
            OnRepeatModeChanged(_mainWindow.CurrentRepeatModeName);
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
        // КРИТИЧНО: этот обработчик может выстрелить ещё ВНУТРИ InitializeComponent(), до того,
        // как конструктор вообще дошёл до "_mainWindow = mainWindow;" — вот почему приложение
        // падало с NullReferenceException при каждом входе в мини-режим. Причина в самом WPF:
        // как только XAML-парсер применяет к слайдеру Minimum="0.3", RangeBase.OnMinimumChanged
        // тут же коэрсит Value (по умолчанию 0, то есть меньше нового минимума) до 0.3 и
        // СИНХРОННО поднимает событие ValueChanged — прямо посреди разбора XAML, а не после его
        // завершения. На этот момент ни _mainWindow, ни даже собственные поля этого окна дальше
        // по дереву XAML (например MiniOpacityContextValueText, который в разметке идёт ПОСЛЕ
        // самого слайдера) ещё не готовы. Value="1.0" в XAML снимает саму причину коэрсии (1.0
        // уже больше 0.3, WPF не нужно ничего подтягивать) — но проверка ниже оставлена как
        // страховка на случай будущих правок разметки, а не только полагается на это.
        //
        // Флага _isSyncingOpacitySlider тут недостаточно — он объявлен как обычное поле без
        // инициализатора, то есть равен false вплоть до первого явного присваивания, а значит
        // в момент XAML-парсинга он ничего не гвардит. _mainWindow, наоборот, — надёжный маркер
        // "конструктор точно уже отработал": он становится не-null СТРОГО ПОСЛЕ того, как
        // InitializeComponent() полностью завершился (см. MiniPlayerWindow(MainWindow) выше),
        // то есть ровно тогда, когда все именованные элементы XAML уже гарантированно связаны.
        if (_mainWindow == null) return;
        if (_isSyncingOpacitySlider) return;

        MiniOpacityContextValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        _mainWindow.SetMiniPlayerOpacity(e.NewValue);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
    // "Below" (по умолчанию, как было всегда): ControlsPanel — отдельная строка Grid.Row="2"
    // под прогресс-баром, скрытая по умолчанию; при наведении окно физически подрастает
    // (CollapsedHeight → ExpandedHeight), чтобы освободить под неё место.
    //
    // "Overlay" (новый): ControlsPanel переносится в ТУ ЖЕ строку Grid.Row="0", что и
    // HeaderPanel (обложка+название+исполнитель) — при наведении не окно растёт, а сама
    // HeaderPanel прячется и её место в той же самой строке занимают кнопки; Margin у
    // ControlsPanel специально не трогаем — при разработке разметки оказалось, что "0,12,0,6"
    // (подобранное для строки под прогресс-баром) даёт ту же итоговую высоту содержимого
    // (36 + 12 + 6 = 54), что и HeaderPanel (42 обложка + 10 + 2 отступов = 54) — то есть
    // визуально кнопки в overlay-режиме встают ровно на то же место, что и обложка с
    // текстом, без каких-либо дополнительных подгонок отступов.
    public void ApplyButtonsLayoutMode()
    {
        _buttonsOverlayMode = _mainWindow.Settings.MiniPlayerButtonsLayout == "Overlay";

        Grid.SetRow(ControlsPanel, _buttonsOverlayMode ? 0 : 2);

        // Сбрасываем в состояние "курсор снаружи" — даже если мышь на самом деле сейчас
        // висит над окном (маловероятно ровно в момент переключения настройки, но не
        // невозможно): следующий RootBorder_MouseEnter/Leave сам всё поправит, а начинать
        // с заведомо согласованного состояния (обложка видна, кнопки скрыты, окно свёрнуто)
        // надёжнее, чем пытаться угадать, в каком из двух РАЗНЫХ по смыслу "развёрнутых"
        // состояний старого и нового режима мы сейчас находимся.
        HeaderPanel.Visibility = Visibility.Visible;
        ControlsPanel.Visibility = Visibility.Collapsed;
        Height = CollapsedHeight;
    }

    private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Visible;

        if (_buttonsOverlayMode)
            HeaderPanel.Visibility = Visibility.Collapsed;
        else
            Height = ExpandedHeight;
    }

    private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Collapsed;

        if (_buttonsOverlayMode)
            HeaderPanel.Visibility = Visibility.Visible;
        else
            Height = CollapsedHeight;
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

        _mainWindow.TrackInfoChanged -= OnTrackInfoChanged;
        _mainWindow.ProgressChanged -= OnProgressChanged;
        _mainWindow.PlaybackStateChanged -= OnPlaybackStateChanged;
        _mainWindow.VolumeChanged -= OnVolumeChanged;
        _mainWindow.RepeatModeChanged -= OnRepeatModeChanged;
        _mainWindow.ShuffleStateChanged -= OnShuffleStateChanged;
        base.OnClosed(e);
    }
}
