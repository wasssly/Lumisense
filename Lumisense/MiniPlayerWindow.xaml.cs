using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    private IntPtr _hwnd;

    // Снимок состояния на МОМЕНТ НАЧАЛА текущего перетаскивания: позиция курсора и
    // прямоугольник окна. Все дальнейшие расчёты позиции внутри одного перетаскивания
    // ведутся от этого снимка (а не от прямоугольника, предложенного предыдущим WM_MOVING).
    // Это и есть исправление бага "нельзя утащить окно от края": раньше каждое новое
    // сообщение WM_MOVING отталкивалось от уже прижатой к краю позиции окна, поэтому
    // небольшое движение мыши почти всегда снова попадало в зону прилипания и окно
    // не двигалось с места. Теперь позиция всегда считается как "чистое" смещение курсора
    // от точки начала перетаскивания — оно растёт вместе с реальным движением мыши и
    // корректно выводит окно за пределы зоны прилипания.
    private bool _isDragging;
    private POINT _dragStartCursor;
    private RECT _dragStartRect;

    public MiniPlayerWindow(MainWindow mainWindow)
    {
        InitializeComponent();

        _mainWindow = mainWindow;
        _mainWindow.TrackInfoChanged += OnTrackInfoChanged;
        _mainWindow.ProgressChanged += OnProgressChanged;
        _mainWindow.PlaybackStateChanged += OnPlaybackStateChanged;
        _mainWindow.VolumeChanged += OnVolumeChanged;

        Height = CollapsedHeight;

        // Сразу отображаем текущее состояние плеера
        OnTrackInfoChanged(_mainWindow.CurrentTitle, _mainWindow.CurrentArtist, _mainWindow.CurrentArtBrush);
        OnPlaybackStateChanged(_mainWindow.IsPlayingNow);

        // Название могло быть длинным ещё до открытия мини-плеера — пересчитываем бегущую
        // строку после первого прохода layout, когда TitleClipBorder.ActualWidth уже известен.
        Loaded += (_, _) => UpdateTitleMarquee();
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

    // ---------- Бегущая строка названия трека ----------
    //
    // Название показывается статично, пока помещается в отведённые 140px. Если оно длиннее —
    // запускаем бесконечную анимацию TranslateTransform.X: пауза в начале (успеть прочитать
    // начало) → плавный проезд до конца строки → пауза в конце → проезд обратно → снова пауза,
    // и по кругу. Скорость (px/сек) одинаковая для любых названий — едет не "название целиком
    // за секунду", а с постоянной скоростью, поэтому длинные названия просто едут дольше.
    //
    // Ширину текста меряем через собственный Measure() у TitleText, а не через
    // TitleText.ActualWidth: ActualWidth доступен только после прохода layout, а
    // обновлять её нужно сразу же в момент смены трека (в том числе один раз ещё до
    // Loaded — см. конструктор), не дожидаясь лишнего кадра. Measure() с бесконечным
    // доступным размером как раз даёт "естественную" ширину текста без ожидания layout.
    //
    // Раньше эта ширина вычислялась вручную через отдельный FormattedText с Typeface,
    // собранным из свойств TitleText. Это в целом похожий, но НЕ гарантированно
    // идентичный способ измерения: FormattedText разрешает шрифт (включая переменные
    // шрифты вроде "Segoe UI Variable", которые использует Fluent-тема) по своему пути,
    // который может чуть разойтись с тем, что реально рисует сам TextBlock. Из-за этого
    // дистанция прокрутки иногда получалась чуть короче настоящей ширины строки, и при
    // докрутке до правого края бегущая строка останавливалась, не дожидаясь буквально
    // нескольких последних пикселей текста — конец названия трека выглядел обрезанным.
    // Measure() того же самого TitleText, который потом и рисуется, эту рассинхронизацию
    // исключает: ширина всегда ровно та, что использует сам элемент при отрисовке.
    // MarqueeEndBufferPx поверх этого — небольшой запас на случай, если правый край
    // глифа (засечки, курсив, антиалиасинг) визуально чуть выходит за расчётную ширину.
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

    // Подставляем актуальное состояние настроек прямо перед показом меню — на случай, если
    // закрепление/топмост поменяли в другом месте (например, в окне настроек) уже после
    // того, как это меню было создано.
    private void MiniPlayerContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        PinnedMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerPinned;
        TopmostMenuItem.IsChecked = _mainWindow.Settings.MiniPlayerAlwaysOnTop;
    }

    private void PinnedMenuItem_Click(object sender, RoutedEventArgs e)
        => _mainWindow.SetMiniPlayerPinned(PinnedMenuItem.IsChecked);

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
        => _mainWindow.SetMiniPlayerTopmost(TopmostMenuItem.IsChecked);

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

    private void RootBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Visible;
        Height = ExpandedHeight;
    }

    private void RootBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        ControlsPanel.Visibility = Visibility.Collapsed;
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
        _mainWindow.TrackInfoChanged -= OnTrackInfoChanged;
        _mainWindow.ProgressChanged -= OnProgressChanged;
        _mainWindow.PlaybackStateChanged -= OnPlaybackStateChanged;
        _mainWindow.VolumeChanged -= OnVolumeChanged;
        base.OnClosed(e);
    }
}
