using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lumisense;

// Мини-плеер «остров»: чёрная капсула у верхнего края экрана. При смене трека она на несколько секунд раскрывается в карточку,
// по клику или вытягиванию вниз — в плеер. Форму ведут пружины; содержимое видов подгоняется под неё и переходит с размытием.
internal partial class IslandPlayerWindow : Window
{
    private readonly record struct Shape(double Width, double Height, double Radius);
    private readonly record struct CoverSpot(double Left, double Top, double Size);

    private enum Grab { None, Held, Pull, Lean }

    private enum State { Compact, Toast, Open, Volume, Menu }

    private const double HostWidth = 640;
    private const double MinPill = 24, MinScale = 0.01;
    private const double CompactMaxHeight = 40;
    private const double ShadowFadeHeight = 50;
    private const double HoverScale = 1.07, PressedScale = 0.93, PressedOpenScale = 0.975;
    private const double MinViewScale = 0.5, MaxViewScale = 1.15;
    private const double MotionBlurSpeed = 650, MaxMotionBlur = 4, MinMotionBlur = 0.5;
    private const double ViewBlur = 12, FadeOutScale = 0.9, FadeInScale = 0.86;
    private const double DragThreshold = 5, DragFollow = 0.9, MaxPull = 96, MaxLean = 44;
    private const double PullWidening = 0.4, PullRounding = 0.3, LeanStretch = 0.6;
    private const double PullToOpen = 42, LeanToSkip = 40;
    private const double PausedCoverScale = 0.85, CoverRadiusRatio = 0.22, RoundCoverRadiusRatio = 0.5;
    private const double RepeatDragDistance = 14, LyricRowHeight = 40;
    private const double VolumeStep = 0.02, AwayMargin = 30;
    private const double VolumeMax = 0.999, VolumeMin = 0.001, VolumeOvershoot = 7, VolumeOvershootSquash = 0.22, VolumeTrackWidth = 162;
    private static readonly TimeSpan OvershootHold = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan VolumeDuration = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KeepOnTopInterval = TimeSpan.FromSeconds(1);

    private static readonly Shape CompactShape = new(210, 34, 17);
    // Круглый вид свёрнутого острова: той же высоты, что капсула, но без текста, только обложка.
    private static readonly Shape CircleShape = new(34, 34, 17);
    private static readonly Shape ToastShape = new(340, 68, 30);
    private static readonly Shape OpenShape = new(400, 176, 40);
    private static readonly Shape OpenLyricsShape = new(440, 412, 40);
    private const double MenuRowHeight = 38, MenuPadding = 24;
    private static readonly Shape VolumeShape = new(250, 34, 17);
    private static readonly CoverSpot CompactCover = new(7, 6, 22);
    private static readonly CoverSpot CircleCover = new(2, 2, 30);
    private static readonly CoverSpot ToastCover = new(12, 12, 44);
    private static readonly CoverSpot OpenCover = new(20, 20, 64);
    private static readonly TimeSpan SettingsOpenDelay = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3.2);
    private static readonly TimeSpan CoverSlide = TimeSpan.FromMilliseconds(460);
    private static readonly TimeSpan FadeInDelay = TimeSpan.FromMilliseconds(70);
    private static readonly double[] LyricOpacity = [0.25, 0.5, 1, 0.5, 0.25];
    private static readonly double[] LyricScale = [0.88, 0.92, 1.08, 0.92, 0.88];
    private static readonly TimeSpan LyricSlide = TimeSpan.FromMilliseconds(420);
    private static readonly Color DefaultRim = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
    private static readonly Brush DimBrush = CreateDimBrush();

    private readonly IMiniPlayerHost _host;
    private readonly SpringValue _width = NewSpring(CompactShape.Width, 330, 22);
    private readonly SpringValue _height = NewSpring(CompactShape.Height, 220, 22);
    private readonly SpringValue _radius = NewSpring(CompactShape.Radius, 300, 24);
    private readonly SpringValue _scale = NewSpring(1, 320, 20);
    private readonly SpringValue _lean = NewSpring(0, 230, 15);
    private readonly SpringValue _coverLeft = NewSpring(CompactCover.Left, 220, 22);
    private readonly SpringValue _coverTop = NewSpring(CompactCover.Top, 220, 22);
    private readonly SpringValue _coverSize = NewSpring(CompactCover.Size, 220, 22);
    private readonly SpringValue _coverScale = NewSpring(1, 240, 25);
    private readonly SpringValue _away = NewSpring(0, 260, 28);
    private readonly SpringValue _overshoot = NewSpring(0, 420, 18);
    private readonly SpringValue[] _springs;
    private readonly BlurEffect _motionBlur = new() { Radius = 0, RenderingBias = RenderingBias.Performance };
    private readonly TextBlock[] _lyricLines;
    private readonly TranslateTransform _lyricsShift = new();
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = ToastDuration };
    private readonly DispatcherTimer _volumeTimer = new() { Interval = VolumeDuration };
    private readonly DispatcherTimer _awayTimer = new();
    private readonly DispatcherTimer _overshootTimer = new() { Interval = OvershootHold };
    private readonly Dictionary<TextBlock, string> _pendingText = new();
    private readonly DispatcherTimer _keepTimer = new(DispatcherPriority.Background) { Interval = KeepOnTopInterval };
    private readonly FrameworkElement[] _views;
    private readonly SolidColorBrush _rimBrush = new(DefaultRim);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private State _state = State.Compact, _volumeReturn = State.Compact;
    private bool _userHidden, _fullscreenHidden;
    private TimeSpan _loadedAt;
    private bool _hovered, _pressed, _morphing, _isPlaying, _isSeeking, _loopRunning, _playbackShown;
    private Grab _grab;
    private Point _grabStart;
    private double _pull, _leanDrag, _lastRatio, _repeatDragStart;
    private bool _lyricsShown, _repeatClickSuppressed;
    private int _lyricsLine;
    private LyricsDocument _lyrics = LyricsDocument.Empty;
    // Варианты для кнопки «Другой вариант» (как в окне Now Playing): известные совпадения по убыванию качества.
    private readonly List<OnlineLyricsResult> _lyricsVariants = [];
    private string? _lyricsVariantsPath;
    private int _lyricsVariantIndex = -1;
    private bool _lyricsVariantsLrcLibLoaded, _lyricsVariantsExtraLoaded, _lyricsVariantBusy;
    private string? _lyricsPath, _seekLinesPath;
    private CancellationTokenSource? _seekLinesCts;
    private CancellationTokenSource? _lyricsCts;
    private Brush _accentBrush = Brushes.White;
    private string _lastTitle = "";
    private Brush? _shownArt;
    private System.Windows.Controls.Border? _coverLayer;
    private double _clipSize = -1, _clipRadius = -1;
    private TimeSpan _lastFrame = TimeSpan.MinValue;

    public IslandPlayerWindow(IMiniPlayerHost host)
    {
        _host = host;
        InitializeComponent();
        _springs = [_width, _height, _radius, _scale, _lean, _coverLeft, _coverTop, _coverSize, _coverScale, _away, _overshoot];
        _views = [CompactView, ToastView, VolumeView, OpenView, MenuView];
        _lyricLines = [LyricLine0, LyricLine1, LyricLine2, LyricLine3, LyricLine4];
        LyricsLines.RenderTransform = _lyricsShift;
        foreach (var line in _lyricLines)
        {
            line.RenderTransformOrigin = new Point(0.5, 0.5);
            line.RenderTransform = new ScaleTransform();
        }
        ApplyLyricStyles();
        // Текст при смене трека гаснет и появляется со сдвигом; сдвиг лежит на самом TextBlock.
        foreach (var block in new[] { CompactTitle, ToastTitle, ToastArtist, OpenTitle, OpenArtist })
            block.RenderTransform = new TranslateTransform();
        foreach (var view in _views)
        {
            view.RenderTransformOrigin = new Point(0.5, 0.5);
            view.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(1, 1), new ScaleTransform(1, 1), new TranslateTransform() }
            };
        }
        Body.Stroke = _rimBrush;
        RenderOptions.SetBitmapScalingMode(CoverHost, BitmapScalingMode.HighQuality);

        IslandBars.Level = () => _host.AudioLevelMeter?.NormalizedLevel ?? 0;
        IslandBars.Spectrum = bands => _host.AudioLevelMeter?.ReadSpectrum(bands) ?? false;
        OpenBars.Level = IslandBars.Level;
        OpenBars.Spectrum = IslandBars.Spectrum;
        IslandBackdropLayer.Spectrum = IslandBars.Spectrum;
        IslandBackdropLayer.Rounding = OpenShape.Radius;
        SetupMenuSliders();
        foreach (FrameworkElement row in MenuStack.Children.OfType<FrameworkElement>())
        {
            var target = row;
            row.MouseEnter += (_, _) => MoveMenuHighlight(target);
        }

        MenuView.MouseLeave += (_, _) => HideMenuHighlight();
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (IsCardOpen && !_hovered && _grab == Grab.None && !_lyricsShown) SetState(State.Compact);
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (_state == State.Toast && _grab == Grab.None) SetState(State.Compact);
        };
        _volumeTimer.Tick += (_, _) =>
        {
            _volumeTimer.Stop();
            if (_state == State.Volume) SetState(_volumeReturn);
        };
        _awayTimer.Tick += (_, _) =>
        {
            _awayTimer.Stop();
            _userHidden = false;
            UpdateAway();
        };
        _keepTimer.Tick += KeepTimer_Tick;
        _overshootTimer.Tick += (_, _) =>
        {
            _overshootTimer.Stop();
            _overshoot.Target = 0;
            StartLoop();
        };
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
        Loaded += (_, _) =>
        {
            PositionOnMonitor();
            RefreshAccent();
            ApplySettingsLive();
            ApplyViewStateInstantly();
            _keepTimer.Start();
            _loadedAt = _clock.Elapsed;
            UpdateTrackInfo(_host.CurrentTitle, _host.CurrentArtist, _host.CurrentArtBrush, announce: false);
            UpdatePlayback(_host.PlaybackState.Current);
            RefreshControlStates();
            ApplyShape();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionOnMonitor));

        _host.TrackInfoChanged += OnTrackInfoChanged;
        _host.PlaybackState.Changed += OnPlaybackChanged;
        _host.VolumeChanged += OnHostVolumeChanged;
        _host.RepeatModeChanged += OnRepeatModeChanged;
        _host.ShuffleStateChanged += OnShuffleStateChanged;
        FavoritesChangeNotifier.Instance.PropertyChanged += OnFavoritesChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += OnWindowClosed;
    }

    private static Brush CreateDimBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
    }

    private static SpringValue NewSpring(double value, double stiffness, double damping)
    {
        var spring = new SpringValue { Value = value, Target = value };
        Tune(spring, stiffness, damping);
        return spring;
    }

    // Жёсткость и демпфирование заданы как у обычной пружины с трением; SpringValue хранит коэффициент демпфирования.
    private static void Tune(SpringValue spring, double stiffness, double damping)
    {
        spring.Stiffness = stiffness;
        spring.DampingRatio = damping / (2 * Math.Sqrt(stiffness));
    }

    private static double DampingOf(SpringValue spring) => spring.DampingRatio * 2 * Math.Sqrt(spring.Stiffness);

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _host.TrackInfoChanged -= OnTrackInfoChanged;
        _host.PlaybackState.Changed -= OnPlaybackChanged;
        _host.VolumeChanged -= OnHostVolumeChanged;
        _host.RepeatModeChanged -= OnRepeatModeChanged;
        _host.ShuffleStateChanged -= OnShuffleStateChanged;
        FavoritesChangeNotifier.Instance.PropertyChanged -= OnFavoritesChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CancelLyricsLoad();
        _host.SetIslandAudioLevelWanted(false);
        _collapseTimer.Stop();
        _toastTimer.Stop();
        _volumeTimer.Stop();
        _awayTimer.Stop();
        _overshootTimer.Stop();
        _keepTimer.Stop();
        StopLoop();
    }

    private AppSettings Settings => _host.Settings;

    private bool ReduceMotion => AccessibilityPreferences.ShouldReduceMotion(Settings, AnimationAreas.Island) || _host.EffectiveGameOverlayCompatibilityEnabled;

    private double UserScale => Math.Clamp(Settings.IslandScalePercent, 80, 120) / 100.0;

    // ----- Положение окна -----

    // Позиция задаётся в пикселях через SetWindowPos: Left/Top в DIP зависят от DPI монитора, на котором окно оказалось
    // до первого показа, и на смешанных DPI остров уезжал бы с центра.
    public void PositionOnMonitor()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return;

        var screen = IslandMonitorResolver.Pick(screens, Settings.IslandMonitor, s => s.DeviceName, s => s.Primary);
        var area = screen.WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        int widthPx = (int)Math.Round(Width * dpi.DpiScaleX);
        int x = area.Left + (area.Width - widthPx) / 2;
        SetWindowPos(handle, IntPtr.Zero, x, area.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PositionOnMonitor));

    // Остров не должен забирать фокус у игры или редактора: клики по нему работают и без активации окна.
    private void ApplyNoActivateStyle()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void ApplyOverlayCompatibilityLive(bool enabled)
    {
        if (enabled) SnapToTargets();
        UpdateEqualizerState();
    }

    public void RefreshAccent()
    {
        var brush = new SolidColorBrush(_host.GetResolvedAccentColor());
        brush.Freeze();
        IslandBars.Fill = OpenBars.Fill = brush.Color;
        MenuSpeedSlider.Accent = MenuPitchSlider.Accent = brush.Color;
        SeekSegments.Accent = brush.Color;
        IslandBackdropLayer.Tint(brush.Color);
        _accentBrush = brush;
        RefreshControlStates();
        UpdateRim();
    }

    // Применяет настройки острова на лету: масштаб, отступ, полоски, обводку и режим эффектов.
    public void ApplySettingsLive()
    {
        IslandOffset.Y = Math.Clamp(Settings.IslandTopGap, 0, 40) + _away.Value;
        UpdateAway(true);
        CompactView.Width = ShapeOf(State.Compact).Width;
        ToastView.Width = ShapeOf(State.Toast).Width;
        OpenView.Width = ShapeOf(State.Open).Width;
        OpenBars.Visibility = Settings.IslandShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
        // В круге нет места ни для названия, ни для эквалайзера.
        IslandBars.Visibility = Settings.IslandShowEqualizer && !IsCircle ? Visibility.Visible : Visibility.Collapsed;
        CompactTitle.Visibility = IsCircle ? Visibility.Collapsed : Visibility.Visible;
        IslandBars.Dots = OpenBars.Dots = Settings.IslandEqualizerStyle == "Dots";
        IslandBackdropLayer.Mode = Settings.IslandBackdrop switch
        {
            "Glow" => IslandBackdrop.Layers.Glow,
            "Matrix" => IslandBackdrop.Layers.Matrix,
            "Wave" => IslandBackdrop.Layers.Wave,
            _ => IslandBackdrop.Layers.None,
        };
        bool segments = Settings.IslandSeekStyle == "Segments";
        SeekSegments.Visibility = segments ? Visibility.Visible : Visibility.Collapsed;
        RefreshSeekLines();
        SeekTrack.Visibility = SeekFill.Visibility = segments ? Visibility.Collapsed : Visibility.Visible;
        if (!Settings.IslandTrackToast && _state == State.Toast) SetState(State.Compact);
        UpdateRim();
        UpdateEqualizerState();
        ApplyMenuRows();
        RefreshControlStates();
        UpdateTargets();
        if (!_loopRunning) ApplyShape();
    }

    // Обводка берёт цвет акцента, пока что-то играет; иначе — нейтральная.
    private void UpdateRim()
    {
        var accent = _host.GetResolvedAccentColor();
        var target = Settings.IslandAccentRim && _isPlaying ? Color.FromArgb(0xC0, accent.R, accent.G, accent.B) : DefaultRim;
        if (ReduceMotion)
        {
            _rimBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _rimBrush.Color = target;
            return;
        }

        _rimBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(450)));
    }

    // ----- Состояния -----

    // Ширина свёрнутого, раскрытого острова и карточки трека меняется настройкой; меню и громкость остаются прежними.
    private double WidthFactor => Math.Clamp(Settings.IslandWidthPercent, 80, 130) / 100.0;

    private bool IsCircle => Settings.IslandShape == "Circle";

    private Shape ShapeOf(State state)
    {
        // Ширина и подгонка под строку текста песни относятся к капсуле; круг всегда остаётся кругом.
        if (state == State.Compact && IsCircle) return CircleShape;

        var shape = BaseShapeOf(state);
        double width = shape.Width * WidthFactor;
        // Со строкой текста песни свёрнутый остров растёт ровно под неё.
        if (state == State.Compact && _compactLyricsWidth is { } fitted) width = Math.Max(width, fitted);
        return state is State.Compact or State.Toast or State.Open ? shape with { Width = width } : shape;
    }

    private Shape BaseShapeOf(State state) => state switch
    {
        State.Toast => ToastShape,
        State.Open => _lyricsShown ? OpenLyricsShape : OpenShape,
        State.Volume => VolumeShape,
        State.Menu => new Shape(270, VisibleMenuRows * MenuRowHeight + MenuPadding, 32),
        _ => CompactShape
    };

    private CoverSpot SpotOf(State state) => state switch
    {
        State.Toast => ToastCover,
        State.Open => OpenCover,
        _ => IsCircle ? CircleCover : CompactCover
    };

    private FrameworkElement ViewOf(State state) => state switch
    {
        State.Toast => ToastView,
        State.Open => OpenView,
        State.Volume => VolumeView,
        State.Menu => MenuView,
        _ => CompactView
    };

    private void SetState(State next)
    {
        if (_state == next) return;

        if (next == State.Menu) ApplyMenuRows();
        if (next == State.Open) ShowOpenVolume(_host.CurrentVolume);
        // Текст песни, оставленный включённым при сворачивании, возвращается вместе с раскрытым островом.
        if (next == State.Open && _compactLyricsOn && !_lyricsShown) SetLyricsShown(true);

        var from = ShapeOf(_state);
        var to = ShapeOf(next);
        _state = next;
        if (next != State.Open && _lyricsShown)
        {
            // Найденный синхронный текст остаётся во включённом виде: свёрнутый остров показывает его строки.
            _compactLyricsOn = _lyrics.Kind == LyricsKind.Synced && _lyrics.Lines.Count > 0;
            SetLyricsShown(false);
        }
        if (next == State.Menu)
        {
            RefreshMenuText();
            // Наведённый остров увеличен на HoverScale; его плавный возврат к 1 масштабировал бы текст меню на каждом кадре раскрытия.
            _scale.Value = 1;
            _scale.Velocity = 0;
        }

        // Растущая форма движется мягче и с небольшим перелётом, сжимающаяся — быстрее и жёстче.
        bool growing = to.Width * to.Height >= from.Width * from.Height;
        Tune(_width, growing ? 330 : 260, growing ? 22 : 27);
        Tune(_height, growing ? 220 : 400, growing ? 22 : 33);
        // Карточка при смене трека раскрывается спокойнее, чем плеер по клику: почти без перелёта.
        if (next == State.Toast)
        {
            Tune(_width, 300, 28);
            Tune(_height, 240, 27);
        }
        foreach (var spring in new[] { _coverLeft, _coverTop, _coverSize }) Tune(spring, _height.Stiffness, DampingOf(_height));

        BeginMorph();
        SwitchView(ViewOf(next));
        if (next == State.Open) UpdatePlayback(LiveSnapshot());
        if (next == State.Compact) UpdateCompactLyric(_host.CurrentPlaybackSeconds, animate: false);
        UpdateEqualizerState();
        UpdateCoverScaleTarget();
        _toastTimer.Stop();
        _collapseTimer.Stop();
        _volumeTimer.Stop();
        if (next == State.Toast) _toastTimer.Start();
        else if ((next == State.Open || next == State.Menu) && !_hovered && Settings.IslandAutoCollapse && !_lyricsShown) StartCollapseTimer();
        UpdateTargets();
    }

    // Кеш в битмап на время анимации не используется: при его снятии текст перерисовывался с другим сглаживанием и «дёргался».
    // Время сворачивания берётся из настроек при каждом запуске, чтобы изменение действовало сразу.
    private void StartCollapseTimer()
    {
        _collapseTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.IslandCollapseSeconds, 1, 15));
        _collapseTimer.Start();
    }

    private void BeginMorph() => _morphing = true;

    private void UpdateTargets()
    {
        var shape = ShapeOf(_state);
        var spot = SpotOf(_state);
        bool compact = _state == State.Compact;
        double pull = RubberBand(_pull, MaxPull);
        double lean = Math.Sign(_leanDrag) * RubberBand(Math.Abs(_leanDrag), MaxLean);
        bool dragging = _grab is Grab.Pull or Grab.Lean;

        _width.Target = shape.Width + pull * PullWidening + Math.Abs(lean) * LeanStretch;
        _height.Target = shape.Height + pull;
        _radius.Target = shape.Radius + pull * PullRounding;
        _lean.Target = lean;
        _coverLeft.Target = spot.Left;
        _coverTop.Target = spot.Top + pull / 2;
        _coverSize.Target = spot.Size;
        if (dragging) TuneForDrag(true);

        _scale.Target = dragging || ReduceMotion ? 1 : _pressed ? (compact ? PressedScale : PressedOpenScale) : _hovered && compact ? HoverScale : 1;

        if (ReduceMotion) SnapToTargets();
        else StartLoop();
    }

    // Чем дальше тянут, тем слабее отклик: растяжение быстро замедляется у предела.
    private static double RubberBand(double distance, double limit) => limit * (1 - 1 / (distance * DragFollow / limit + 1));

    private void TuneForDrag(bool tight)
    {
        Tune(_width, tight ? 900 : 300, tight ? 60 : 21);
        Tune(_height, tight ? 900 : 300, tight ? 60 : 21);
        Tune(_radius, tight ? 900 : 300, tight ? 60 : 30);
        Tune(_lean, tight ? 900 : 230, tight ? 60 : 15);
        foreach (var spring in new[] { _coverLeft, _coverTop, _coverSize }) Tune(spring, tight ? 900 : 300, tight ? 60 : 21);
    }

    // В раскрытом плеере обложка уменьшается на паузе и «пружинит» обратно при возобновлении.
    private void UpdateCoverScaleTarget()
    {
        double to = _state == State.Volume || _state == State.Menu ? 0 : _state == State.Open && !_isPlaying ? PausedCoverScale : 1;
        if (to == _coverScale.Target) return;

        bool lively = _state == State.Open && to == 1;
        Tune(_coverScale, lively ? 260 : 240, lively ? 13 : 25);
        _coverScale.Target = to;
        if (ReduceMotion) SnapToTargets();
        else StartLoop();
    }

    // ----- Кадровый цикл -----

    private void StartLoop()
    {
        if (_loopRunning) return;

        _loopRunning = true;
        _lastFrame = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopLoop()
    {
        if (!_loopRunning) return;

        _loopRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastFrame) return;

        double delta = _lastFrame == TimeSpan.MinValue ? 1.0 / 60 : (args.RenderingTime - _lastFrame).TotalSeconds;
        _lastFrame = args.RenderingTime;

        bool moving = false;
        foreach (var spring in _springs) moving |= spring.Step(delta);
        ApplyShape();
        if (moving) return;

        EndMorph();
        StopLoop();
    }

    private void SnapToTargets()
    {
        foreach (var spring in _springs)
        {
            spring.Value = spring.Target;
            spring.Velocity = 0;
        }
        _morphing = true;
        ApplyShape();
        EndMorph();
        StopLoop();
        ApplyViewStateInstantly();
    }

    private void ApplyViewStateInstantly()
    {
        var current = ViewOf(_state);
        foreach (var view in _views)
        {
            bool isCurrent = view == current;
            view.BeginAnimation(OpacityProperty, null);
            view.Opacity = isCurrent ? 1 : 0;
            view.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
            view.IsHitTestVisible = isCurrent;
            view.Effect = null;
            FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleXProperty, null);
            FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleYProperty, null);
            FadeScaleOf(view).ScaleX = FadeScaleOf(view).ScaleY = 1;
        }
    }

    // ----- Форма -----

    private void ApplyShape()
    {
        double w = Math.Max(_width.Value, MinPill), h = Math.Max(_height.Value, MinPill);
        double r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);
        var pill = new Rect((HostWidth - w) / 2, 0, w, h);

        var outline = IslandOutline.Create(pill, r);
        Body.Data = outline;
        Host.Clip = outline;

        double shadow = Math.Clamp((h - CompactMaxHeight) / ShadowFadeHeight, 0, 1);
        ShadowFar.Data = ShadowNear.Data = outline;
        ShadowFar.Opacity = shadow * 0.10;
        ShadowNear.Opacity = shadow * 0.16;

        FitViews(w, h);
        ApplyMotionBlur();

        double scale = Math.Max(_scale.Value, MinScale) * UserScale;
        IslandScale.ScaleX = IslandScale.ScaleY = scale;
        // Остаток пружины в доли пикселя сдвигал бы текст на полпикселя и размывал его.
        IslandLean.X = Math.Abs(_lean.Value) < 0.05 ? 0 : _lean.Value;
        IslandOffset.Y = Math.Clamp(Settings.IslandTopGap, 0, 40) + _away.Value;

        double size = Math.Max(_coverSize.Value, 1);
        double radiusRatio = Settings.IslandRoundCover ? RoundCoverRadiusRatio : CoverRadiusRatio;
        // Обложка в круге круглая, а при раскрытии острова плавно возвращается к обычной форме: пока она растёт от 30 до 44 px.
        if (IsCircle) radiusRatio = RoundCoverRadiusRatio + (radiusRatio - RoundCoverRadiusRatio) * Math.Clamp((size - CircleCover.Size) / 14, 0, 1);
        double radius = size * radiusRatio;
        if (Math.Abs(size - _clipSize) > 0.05 || Math.Abs(radius - _clipRadius) > 0.05)
        {
            _clipSize = size;
            _clipRadius = radius;
            CoverHost.Width = CoverHost.Height = size;
            CoverHost.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        }
        double push = Math.Max(_overshoot.Value, -VolumeOvershoot);
        VolumeStretch.ScaleX = 1 + push / VolumeTrackWidth;
        VolumeStretch.ScaleY = 1 - push / VolumeOvershoot * VolumeOvershootSquash;
        CoverScale.ScaleX = CoverScale.ScaleY = Math.Max(_coverScale.Value, MinScale);
        Canvas.SetLeft(CoverHost, pill.Left + _coverLeft.Value);
        Canvas.SetTop(CoverHost, _coverTop.Value);
    }

    // Пока форма меняется, вид подгоняется под неё: уменьшается и сдвигается, чтобы не вылезать за края.
    private void FitViews(double w, double h)
    {
        if (!_morphing) return;

        foreach (var view in _views)
        {
            // Меню и плеер открывает рост формы, а не масштаб: масштабируемые значки и текст дрожат.
            if (view.Visibility != Visibility.Visible || IsSteady(view)) continue;
            double scale = Math.Clamp(Math.Min(w / view.Width, h / view.Height), MinViewScale, MaxViewScale);
            var fit = FitScaleOf(view);
            fit.ScaleX = fit.ScaleY = scale;
            OffsetOf(view).Y = (h - view.Height) / 2;
        }
    }

    private void EndMorph()
    {
        if (!_morphing || _grab == Grab.Pull) return;

        _morphing = false;
        foreach (var view in _views)
        {
            FitScaleOf(view).ScaleX = FitScaleOf(view).ScaleY = 1;
            OffsetOf(view).Y = 0;
        }
        Host.Effect = null;
    }

    // Размытие по скорости растяжения даёт ощущение движения, но это пиксельный эффект на каждом кадре,
    // поэтому он включается отдельной настройкой и отключается при «уменьшении движения».
    private void ApplyMotionBlur()
    {
        double smear = 0;
        if (Settings.IslandMotionBlur && _morphing && _grab == Grab.None && !ReduceMotion)
        {
            double speed = Math.Sqrt(_width.Velocity * _width.Velocity + _height.Velocity * _height.Velocity);
            smear = Math.Min(speed / MotionBlurSpeed, MaxMotionBlur);
        }

        if (smear < MinMotionBlur)
        {
            if (Host.Effect is not null) Host.Effect = null;
            return;
        }

        _motionBlur.Radius = Math.Round(smear * 2) / 2;
        if (!ReferenceEquals(Host.Effect, _motionBlur)) Host.Effect = _motionBlur;
    }

    // Виды с мелким текстом и значками не масштабируются при раскрытии: их открывает только рост формы с обрезкой.
    private bool IsSteady(FrameworkElement view) => view == MenuView || view == OpenView;

    private static ScaleTransform FadeScaleOf(FrameworkElement view) => (ScaleTransform)((TransformGroup)view.RenderTransform).Children[0];
    private static ScaleTransform FitScaleOf(FrameworkElement view) => (ScaleTransform)((TransformGroup)view.RenderTransform).Children[1];
    private static TranslateTransform OffsetOf(FrameworkElement view) => (TranslateTransform)((TransformGroup)view.RenderTransform).Children[2];

    // ----- Смена видов -----

    private void SwitchView(FrameworkElement next)
    {
        if (ReduceMotion)
        {
            ApplyViewStateInstantly();
            return;
        }

        foreach (var view in _views)
            if (view != next && view.Visibility == Visibility.Visible) FadeOut(view);
        FadeIn(next);
    }

    private void FadeOut(FrameworkElement view)
    {
        view.IsHitTestVisible = false;
        if (Settings.IslandBlurTransitions)
        {
            var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            view.Effect = blur;
            blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(ViewBlur, TimeSpan.FromMilliseconds(170)));
        }

        if (!IsSteady(view))
        {
            var shrink = new DoubleAnimation(FadeOutScale, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            // Вид мог успеть вернуться (быстрая смена состояний подряд): тогда его не прячем.
            if (view == ViewOf(_state)) return;
            view.Visibility = Visibility.Collapsed;
            view.Effect = null;
        };
        view.BeginAnimation(OpacityProperty, fade);
    }

    private void FadeIn(FrameworkElement view)
    {
        bool fresh = view.Visibility != Visibility.Visible || view.Opacity < 0.05;
        view.Visibility = Visibility.Visible;
        view.IsHitTestVisible = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (Settings.IslandBlurTransitions)
        {
            var blur = new BlurEffect { Radius = ViewBlur, RenderingBias = RenderingBias.Performance };
            view.Effect = blur;
            var unblur = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { BeginTime = FadeInDelay, EasingFunction = ease };
            unblur.Completed += (_, _) =>
            {
                if (ReferenceEquals(view.Effect, blur)) view.Effect = null;
            };
            blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
        }
        else
        {
            view.Effect = null;
        }

        if (!IsSteady(view))
        {
            var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { BeginTime = FadeInDelay, EasingFunction = ease };
            if (fresh) grow.From = FadeInScale;
            FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        if (fresh && IsSteady(view) && !ReduceMotion)
        {
            // Как строки списка в архиве друга: элементы проявляются по очереди и чуть всплывают снизу, сам вид быстро становится видимым.
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
            Reveal(view == OpenView
                ? [OpenTitleStack, OpenSeekRow, LeftSideButton, RightSideButton, OpenTransport, OpenBars]
                : MenuRows.Cast<UIElement>().ToArray());
            return;
        }

        view.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { BeginTime = FadeInDelay });
    }

    private static readonly TimeSpan RevealStagger = TimeSpan.FromMilliseconds(34), RevealRun = TimeSpan.FromMilliseconds(380), RevealShow = TimeSpan.FromMilliseconds(200);
    private const double RevealRise = 7;

    // Элементы появляются по очереди: ждут своей очереди прозрачными, затем растворяются и поднимаются на место.
    private static void Reveal(IReadOnlyList<UIElement> elements)
    {
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var wait = FadeInDelay;
        foreach (var element in elements)
        {
            if (element.Visibility != Visibility.Visible) continue;

            var show = Delayed(0, 1, wait, RevealShow, null);
            show.Completed += (_, _) => element.BeginAnimation(OpacityProperty, null);
            element.BeginAnimation(OpacityProperty, show);

            // Мелкие значки и эквалайзер только проявляются: плавный сдвиг на доли пикселя виден как дрожание.
            var move = element is System.Windows.Controls.Button or IslandEqualizer ? null : element.RenderTransform as TranslateTransform;
            if (move is null && element is not System.Windows.Controls.Button and not IslandEqualizer && element.RenderTransform == Transform.Identity)
                element.RenderTransform = move = new TranslateTransform();
            if (move is { IsFrozen: false })
            {
                var rise = Delayed(RevealRise, 0, wait, RevealRun, ease);
                var target = move;
                rise.Completed += (_, _) => target.BeginAnimation(TranslateTransform.YProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, rise);
            }

            wait += RevealStagger;
        }
    }

    private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, TimeSpan wait, TimeSpan run, IEasingFunction? ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = wait + run };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(wait)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(wait + run), ease));
        return animation;
    }

    // ----- Указатель -----

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovered = true;
        _collapseTimer.Stop();
        UpdateTargets();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovered = _pressed = false;
        UpdateTargets();
        if (IsCardOpen && Settings.IslandAutoCollapse && !_lyricsShown) StartCollapseTimer();
    }

    private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        if (Island.CaptureMouse())
        {
            _grab = Grab.Held;
            _grabStart = e.GetPosition(this);
        }
        UpdateTargets();
    }

    private void Island_MouseMove(object sender, MouseEventArgs e)
    {
        if (_grab == Grab.None) return;

        Vector moved = e.GetPosition(this) - _grabStart;
        if (_grab == Grab.Held)
        {
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold) return;
            _grab = Math.Abs(moved.X) > Math.Abs(moved.Y) ? Grab.Lean : Grab.Pull;
            if (_grab == Grab.Pull) BeginMorph();
        }

        // Вытягивать вниз имеет смысл только свёрнутый остров; открытый и меню на вытягивание не реагируют.
        if (_grab == Grab.Pull) _pull = IsCardOpen ? 0 : Math.Max(moved.Y, 0) / UserScale;
        else _leanDrag = moved.X / UserScale;

        // Вытянули достаточно: раскрываем сразу, не дожидаясь отпускания кнопки, и дальше движение курсора раскрытие не прерывает.
        if (_grab == Grab.Pull && _pull >= PullToOpen && !IsCardOpen)
        {
            _pressed = false;
            ReleaseGrab();
            SetState(State.Open);
        }

        UpdateTargets();
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;

        _pressed = false;
        var grab = _grab;
        double pulled = _pull, leant = _leanDrag;
        ReleaseGrab();

        switch (grab)
        {
            case Grab.Held when _state == State.Open && Settings.IslandCoverPreview && IsOverCover(e):
                _host.ShowCoverPreview(this);
                break;
            case Grab.Held:
                SetState(IsCardOpen ? State.Compact : State.Open);
                break;
            case Grab.Pull when pulled >= PullToOpen && !IsCardOpen:
                SetState(State.Open);
                break;
            case Grab.Lean when Math.Abs(leant) >= LeanToSkip:
                bool toNext = (leant < 0) != Settings.IslandSwipeInverted;
                Skip(toNext ? 1 : -1);
                break;
        }

        UpdateTargets();
    }

    private bool IsOverCover(MouseEventArgs e)
    {
        var point = e.GetPosition(CoverHost);
        return point.X >= 0 && point.Y >= 0 && point.X <= CoverHost.ActualWidth && point.Y <= CoverHost.ActualHeight;
    }

    private void Island_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_grab == Grab.None) return;

        _pressed = false;
        ReleaseGrab();
        UpdateTargets();
    }

    private void ReleaseGrab()
    {
        _grab = Grab.None;
        _pull = _leanDrag = 0;
        TuneForDrag(false);
        BeginMorph();
        if (Island.IsMouseCaptured) Island.ReleaseMouseCapture();
    }

    private void Skip(int direction)
    {
        Nudge(direction > 0 ? NextNudge : PrevNudge, direction);
        if (direction > 0) _host.ExternalNext();
        else _host.ExternalPrev();
    }

    // Значок переключения «подталкивается» в сторону движения и возвращается.
    private void Nudge(TranslateTransform transform, int direction)
    {
        if (ReduceMotion) return;

        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 6, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // ----- Данные трека -----

    private void OnTrackInfoChanged(string title, string artist, Brush? art) => UpdateTrackInfo(title, artist, art, announce: true);

    private void UpdateTrackInfo(string title, string artist, Brush? art, bool announce)
    {
        bool newTrack = title.Length > 0 && title != _lastTitle;
        _lastTitle = title;

        // Анимируется только текст того вида, который сейчас на экране; остальные меняются мгновенно, пока скрыты.
        bool live = announce && !ReduceMotion;
        if (_compactLyricsOn && _host.CurrentTrackPath != _lyricsPath)
        {
            _lyrics = LyricsDocument.Empty;
            _lyricsLine = int.MinValue;
        }

        string compactText = CompactText(_host.CurrentPlaybackSeconds);
        SwapText(CompactTitle, compactText, live && _state == State.Compact, default, () => FitCompactWidth(compactText));
        SwapText(ToastTitle, title, live && _state == State.Toast, default);
        SwapText(ToastArtist, artist, live && _state == State.Toast, TimeSpan.FromMilliseconds(45));
        SwapText(OpenTitle, title, live && _state == State.Open, default);
        SwapText(OpenArtist, artist, live && _state == State.Open, TimeSpan.FromMilliseconds(45));

        if (!ReferenceEquals(art, _shownArt) || _coverLayer is null)
        {
            _shownArt = art;
            ShowCover(art, _host.LastTrackChangeDirection, animate: announce && !ReduceMotion);
        }

        RefreshControlStates();
        if (_state == State.Open) UpdatePlayback(LiveSnapshot());
        RefreshSeekLines();
        if ((_lyricsShown || _compactLyricsOn) && _host.CurrentTrackPath != _lyricsPath) StartLyricsLoad();

        // Состояние воспроизведения при смене трека может прийти позже названия, поэтому «играет» здесь не проверяется.
        if (!announce || !newTrack || !Settings.IslandTrackToast || IsAway
            || (Settings.IslandQuietInGames && _host.EffectiveGameOverlayCompatibilityEnabled)) return;

        // Новый трек: свёрнутый остров на пару секунд раскрывается в карточку, раскрытый плеер остаётся как есть.
        if (_state == State.Compact) SetState(State.Toast);
        else if (_state == State.Toast)
        {
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }

    // Старый текст уходит вверх и гаснет, новый выезжает снизу; замена происходит, пока текст невидим, поэтому без скачка раскладки.
    // applied вызывается, когда новый текст уже стоит на месте (сразу или после исчезновения старого).
    private void SwapText(TextBlock block, string text, bool animate, TimeSpan delay, Action? applied = null, double outMs = 120, double inMs = 260)
    {
        var move = (TranslateTransform)block.RenderTransform;
        if (!animate)
        {
            _pendingText.Remove(block);
            block.BeginAnimation(OpacityProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            block.Text = text;
            block.Opacity = 1;
            move.Y = 0;
            applied?.Invoke();
            return;
        }

        if (block.Text == text)
        {
            applied?.Invoke();
            return;
        }

        _pendingText[block] = text;
        var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        move.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-5, TimeSpan.FromMilliseconds(outMs)) { BeginTime = delay, EasingFunction = easeIn });

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(outMs)) { BeginTime = delay, EasingFunction = easeIn };
        fade.Completed += (_, _) =>
        {
            if (!_pendingText.Remove(block, out string? latest)) return;

            block.Text = latest;
            applied?.Invoke();
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(inMs + 40)) { EasingFunction = easeOut });
            block.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(inMs)) { EasingFunction = easeOut });
        };
        block.BeginAnimation(OpacityProperty, fade);
    }

    // Новая обложка выезжает с той стороны, куда переключили трек, прежняя уезжает в противоположную; первая — просто появляется.
    private void ShowCover(Brush? art, int direction, bool animate)
    {
        var old = _coverLayer;
        var layer = new System.Windows.Controls.Border
        {
            Background = art ?? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            RenderTransform = new TranslateTransform()
        };
        _coverLayer = layer;

        if (!animate || old is null || CoverHost.ActualWidth <= 0)
        {
            CoverHost.Children.Clear();
            CoverHost.Children.Add(layer);
            return;
        }

        double shift = CoverHost.ActualWidth * (direction < 0 ? -1 : 1);
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var leave = new DoubleAnimation(-shift, CoverSlide) { EasingFunction = ease };
        leave.Completed += (_, _) => CoverHost.Children.Remove(old);
        ((TranslateTransform)old.RenderTransform).BeginAnimation(TranslateTransform.XProperty, leave);

        CoverHost.Children.Add(layer);
        ((TranslateTransform)layer.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(shift, 0, CoverSlide) { EasingFunction = ease });
    }

    private void OnPlaybackChanged(PlaybackSnapshot snapshot) => UpdatePlayback(snapshot);

    private PlaybackSnapshot LiveSnapshot() => _host.PlaybackState.Current with
    {
        PositionSeconds = _host.CurrentPlaybackSeconds,
        DurationSeconds = _host.CurrentTrackDurationSeconds,
    };

    private void UpdatePlayback(PlaybackSnapshot snapshot)
    {
        if (_isPlaying != snapshot.IsPlaying || !_playbackShown)
        {
            bool animate = _playbackShown && _state == State.Open && !ReduceMotion;
            _isPlaying = snapshot.IsPlaying;
            _playbackShown = true;
            SwapIcons(_isPlaying ? PlayIcon : PauseIcon, _isPlaying ? PauseIcon : PlayIcon, animate);
            UpdateEqualizerState();
            UpdateCoverScaleTarget();
            UpdateRim();
            if (_state == State.Menu) RefreshMenuText();
        }

        if (_compactLyricsOn && _state == State.Compact) UpdateCompactLyric(snapshot.PositionSeconds);

        // Пока остров не раскрыт, элементы плеера не видны: обновлять их на каждый снимок значит зря гонять раскладку.
        if (_state != State.Open || _isSeeking) return;

        _lastRatio = snapshot.ProgressRatio;
        if (_lyricsShown) UpdateLyricLine(snapshot.PositionSeconds);
        SeekSegments.SetDuration(snapshot.DurationSeconds);
        ElapsedText.Text = FormatTime(snapshot.PositionSeconds);
        RemainingText.Text = "–" + FormatTime(Math.Max(0, snapshot.DurationSeconds - snapshot.PositionSeconds));
        UpdateSeekFill(_lastRatio);
    }

    // Уходящий значок сжимается и гаснет, входящий вырастает с лёгким «отскоком».
    private static void SwapIcons(FrameworkElement leave, FrameworkElement enter, bool animate)
    {
        var leaveScale = (ScaleTransform)leave.RenderTransform;
        var enterScale = (ScaleTransform)enter.RenderTransform;
        var quick = TimeSpan.FromMilliseconds(animate ? 150 : 0);
        var slow = TimeSpan.FromMilliseconds(animate ? 360 : 0);

        var shrink = new DoubleAnimation(0.5, quick) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        leaveScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        leaveScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        leave.BeginAnimation(OpacityProperty, new DoubleAnimation(0, quick));

        var grow = new DoubleAnimation(0.5, 1, slow) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        enterScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        enterScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        enter.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(animate ? 200 : 0)));
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private void UpdateSeekFill(double ratio)
    {
        SeekFill.Width = Math.Max(0, SeekHost.ActualWidth * Math.Clamp(ratio, 0, 1));
        SeekSegments.Ratio = ratio;
    }

    private void SeekHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSeekFill(_lastRatio);

    // ----- Перемотка -----

    private double RatioFromMouse(MouseEventArgs e)
    {
        if (SeekHost.ActualWidth <= 0) return 0;

        double x = e.GetPosition(SeekHost).X;
        if (SeekSegments.Visibility == Visibility.Visible) x = SeekSegments.Snap(x);
        return Math.Clamp(x / SeekHost.ActualWidth, 0, 1);
    }

    private void SeekHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
        SeekHost.CaptureMouse();
        _lastRatio = RatioFromMouse(e);
        UpdateSeekFill(_lastRatio);
        e.Handled = true;
    }

    private void SeekHost_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isSeeking) SeekSegments.SetPointer(null);
    }

    private void SeekHost_MouseMove(object sender, MouseEventArgs e)
    {
        SeekSegments.SetPointer(e.GetPosition(SeekHost).X);
        if (!_isSeeking) return;

        _lastRatio = RatioFromMouse(e);
        UpdateSeekFill(_lastRatio);
    }

    private void SeekHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking) return;

        _isSeeking = false;
        SeekHost.ReleaseMouseCapture();
        _host.ExternalSeekRatio(RatioFromMouse(e));
        e.Handled = true;
    }

    // ----- Кнопки и меню -----

    private void PrevButton_Click(object sender, RoutedEventArgs e) => Skip(-1);
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _host.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => Skip(1);

    // ----- Средняя кнопка, колесо, меню -----

    private bool IsAway => _userHidden || _fullscreenHidden;

    // Плеер и меню — раскрытые карточки: они одинаково сворачиваются и не вытягиваются вниз.
    private bool IsCardOpen => _state == State.Open || _state == State.Menu;

    private void Island_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        e.Handled = true;
        HideTemporarily();
    }

    private void HideTemporarily()
    {
        _userHidden = true;
        _awayTimer.Stop();
        _awayTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.IslandHideSeconds, 3, 60));
        _awayTimer.Start();
        UpdateAway();
    }

    // Остров уезжает за верхний край, если его спрятали вручную или поверх развёрнуто чужое полноэкранное окно, и возвращается пружиной.
    private void UpdateAway(bool force = false)
    {
        double gap = Math.Clamp(Settings.IslandTopGap, 0, 40);
        double target = IsAway ? -(OpenLyricsShape.Height * UserScale + gap + AwayMargin) : 0;
        if (!force && target == _away.Target) return;

        bool leaving = IsAway;
        Tune(_away, leaving ? 260 : 200, leaving ? 28 : 20);
        _away.Target = target;
        if (leaving) SetState(State.Compact);
        if (ReduceMotion) SnapToTargets();
        else StartLoop();
    }

    // Окно с другим Z-порядком могло перекрыть остров, поэтому «поверх всех» подтверждается раз в секунду.
    private void KeepTimer_Tick(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        bool fullscreen = ForegroundFullscreen.CoversMonitorOf(handle);
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            UpdateAway();
        }
        if (!fullscreen) SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsAway || e.Delta == 0) return;

        e.Handled = true;
        bool up = e.Delta > 0;
        double before = _host.CurrentVolume;
        if (up ? before >= VolumeMax : before <= VolumeMin)
        {
            // Упёрлись в предел: полоска упруго растягивается в сторону прокрутки и возвращается.
            VolumeTrack.RenderTransformOrigin = new Point(up ? 0 : 1, 0.5);
            _overshoot.Target = VolumeOvershoot;
            _overshootTimer.Stop();
            _overshootTimer.Start();
            if (ReduceMotion) _overshoot.Target = 0;
            else StartLoop();
            ShowVolume(before);
            return;
        }

        _host.ExternalChangeVolume((up ? 1 : -1) * VolumeStep);
        ShowVolume(_host.CurrentVolume);
    }

    // Громкость меняют клавиши, главное окно или загрузка настроек: при старте событие приходит сразу, его не показываем.
    private void OnHostVolumeChanged(double level)
    {
        ShowOpenVolume(level);
        if (!Settings.IslandExternalVolume || IsAway || !IsLoaded || _clock.Elapsed - _loadedAt < StartupQuiet) return;

        ShowVolume(level);
    }

    // В раскрытом острове громкость показана всегда (слева от эквалайзера); при изменении индикатор ненадолго ярче.
    private const double OpenVolumeRest = 0.6;

    private void ShowOpenVolume(double level, bool flash = false)
    {
        level = Math.Clamp(level, 0, 1);
        OpenVolumeText.Text = ((int)Math.Round(level * 100)).ToString();
        OpenVolumeIcon.Icon = level <= 0 ? "IconSpeakerMute" : level < 0.5 ? "IconSpeaker" : "IconSpeaker2";
        if (!flash || ReduceMotion) return;

        var show = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1900) };
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(OpenVolumeRest, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
        OpenVolume.BeginAnimation(OpacityProperty, show);
    }

    private void ShowVolume(double level)
    {
        level = Math.Clamp(level, 0, 1);
        string percent = ((int)Math.Round(level * 100)).ToString();
        string icon = level <= 0 ? "IconSpeakerMute" : level < 0.5 ? "IconSpeaker" : "IconSpeaker2";

        // В раскрытом плеере форма не меняется: рядом с кнопками на пару секунд появляется маленький индикатор.
        if (_state == State.Open)
        {
            ShowOpenVolume(level, flash: true);
            return;
        }

        bool entering = _state != State.Volume;
        VolumeText.Text = percent;
        VolumeIcon.Icon = icon;

        if (entering || ReduceMotion)
        {
            VolumeFill.BeginAnimation(WidthProperty, null);
            VolumeFill.Width = VolumeFillWidth(level);
        }
        else
        {
            VolumeFill.BeginAnimation(WidthProperty, new DoubleAnimation(VolumeFillWidth(level), TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        if (entering)
        {
            _volumeReturn = State.Compact;
            SetState(State.Volume);
        }
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    // Не уже толщины полосы, чтобы начало оставалось ровным круглым концом.
    private static double VolumeFillWidth(double level) => Math.Max(5, Math.Clamp(level, 0, 1) * VolumeTrackWidth);

    private void Island_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_state != State.Menu)
        {
            ApplyMenuRows();
            if (_visibleMenuRows == 0) return;
        }

        SetState(_state == State.Menu ? State.Compact : State.Menu);
    }

    private int _visibleMenuRows = 10;

    private int VisibleMenuRows => _visibleMenuRows;

    private IEnumerable<FrameworkElement> MenuRows => MenuStack.Children.OfType<FrameworkElement>();

    // Пункты, скрытые в настройках, убираются из меню, а его высота подгоняется под оставшиеся.
    private void ApplyMenuRows()
    {
        ContextMenuOrdering.Apply(MenuStack, ContextMenuOrdering.Effective(Settings, ContextMenuOrdering.Island));
        var hidden = Settings.DisabledIslandMenuActions ?? [];
        int visible = 0;
        foreach (var row in MenuRows)
        {
            bool show = row.Tag is not string id || !hidden.Contains(id);
            row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) visible++;
        }

        _visibleMenuRows = visible;
        MenuView.Height = Math.Max(visible, 1) * MenuRowHeight + MenuPadding;
    }

    private void RefreshMenuText()
    {
        MenuPlayText.Text = LocalizationService.Translate(_isPlaying ? "Пауза" : "Продолжить");
        MenuPlayIcon.Icon = _isPlaying ? "IconPause" : "IconPlay";
        MenuNextText.Text = LocalizationService.Translate("Следующий трек");
        MenuPreviousText.Text = LocalizationService.Translate("Предыдущий трек");
        MenuHideText.Text = LocalizationService.Translate("Скрыть остров");
        MenuExpandText.Text = LocalizationService.Translate("Развернуть плеер");
        MenuSettingsText.Text = LocalizationService.Translate("Настройки острова");
        MenuNowPlayingText.Text = LocalizationService.Translate("Now Playing (полный экран)");
        MenuOverlayText.Text = LocalizationService.Translate("Совместимость с играми и оверлеем");
        RefreshMenuTuning();
    }

    // Скорость и тон в меню острова — ползунки: правая кнопка возвращает обычное значение, колесо двигает на шаг.
    private void SetupMenuSliders()
    {
        MenuSpeedSlider.Minimum = 0.5;
        MenuSpeedSlider.Maximum = 2.0;
        MenuSpeedSlider.Step = MenuSpeedSlider.WheelStep = 0.05;
        MenuSpeedSlider.Default = 1.0;
        MenuSpeedSlider.ValueEdited += value =>
        {
            _host.SetPlaybackRateFromMiniPlayer(Math.Round(value, 2));
            RefreshMenuTuning();
        };

        MenuPitchSlider.Minimum = -12;
        MenuPitchSlider.Maximum = 12;
        MenuPitchSlider.Step = MenuPitchSlider.WheelStep = 1;
        MenuPitchSlider.Default = 0;
        MenuPitchSlider.ValueEdited += value =>
        {
            _host.SetPlaybackPitchFromMiniPlayer(value);
            RefreshMenuTuning();
        };
    }

    private void RefreshMenuTuning()
    {
        MenuSpeedText.Text = $"{LocalizationService.Translate("Скорость")} {Settings.PlaybackSpeed:0.00}×";
        MenuPitchText.Text = $"{LocalizationService.Translate("Тон")} {Settings.PlaybackPitchSemitones:+0.#;-0.#;0}";
        MenuSpeedSlider.Value = Settings.PlaybackSpeed;
        MenuPitchSlider.Value = Settings.PlaybackPitchSemitones;
        MenuOverlayCheck.Opacity = Settings.GameOverlayCompatibilityMode ? 1 : 0.18;
    }

    private void MenuNowPlaying_Click(object sender, RoutedEventArgs e)
    {
        SetState(State.Compact);
        _host.ShowNowPlayingWindow();
    }

    private void MenuOverlay_Click(object sender, RoutedEventArgs e)
    {
        Settings.GameOverlayCompatibilityMode = !Settings.GameOverlayCompatibilityMode;
        _host.ApplyMiniPlayerOverlayCompatibilityLive(_host.EffectiveGameOverlayCompatibilityEnabled);
        BackgroundTask.FireAndForget(SettingsManager.SaveAsync(Settings), "IslandOverlaySave");
        RefreshMenuTuning();
    }

    private void MenuPlayPause_Click(object sender, RoutedEventArgs e) => _host.ExternalPlayPause();

    private void MenuNext_Click(object sender, RoutedEventArgs e) => Skip(1);

    private void MenuPrevious_Click(object sender, RoutedEventArgs e) => Skip(-1);

    private void MenuHide_Click(object sender, RoutedEventArgs e) => HideTemporarily();

    private void MenuExpand_Click(object sender, RoutedEventArgs e) => _host.ExitMiniMode();

    // Подсветка перетекает к строке под курсором и в движении слегка вытягивается; первая подсветка просто проявляется на месте.
    private void MoveMenuHighlight(FrameworkElement row)
    {
        double y = row.TranslatePoint(new Point(0, 0), MenuView).Y;
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(140));
        bool fresh = MenuHighlight.Opacity < 0.05 || ReduceMotion;
        MenuHighlight.BeginAnimation(OpacityProperty, fadeIn);
        if (fresh)
        {
            MenuHighlightMove.BeginAnimation(TranslateTransform.YProperty, null);
            MenuHighlightMove.Y = y;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MenuHighlightMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        var stretch = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.22, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), ease));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), ease));
        MenuHighlightStretch.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);
    }

    private void HideMenuHighlight() =>
        MenuHighlight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        SetState(State.Compact);

        // Окно настроек создаётся долго и рвало бы анимацию сворачивания: открываем его, когда остров уже свернулся.
        var timer = new DispatcherTimer { Interval = ReduceMotion ? TimeSpan.Zero : SettingsOpenDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsLoaded) _host.ShowSettingsWindow("Island");
        };
        timer.Start();
    }

    // ----- Боковые кнопки: избранное, повтор, текст песни -----

    private static string SideMode(string? value, string fallback) => value is "Favorite" or "Repeat" or "Lyrics" ? value : fallback;

    private string ModeOf(System.Windows.Controls.Button button) => ReferenceEquals(button, LeftSideButton)
        ? SideMode(Settings.IslandLeftButton, "Favorite")
        : SideMode(Settings.IslandRightButton, "Lyrics");

    private void RefreshControlStates()
    {
        RefreshSide(LeftSideButton, LeftSideIcon, LeftSideDot);
        RefreshSide(RightSideButton, RightSideIcon, RightSideDot);
    }

    private void RefreshSide(System.Windows.Controls.Button button, SvgPathIcon icon, System.Windows.Shapes.Ellipse dot)
    {
        string function = ModeOf(button);
        bool active;
        switch (function)
        {
            case "Favorite":
                active = _host.CurrentTrackPath is { } path && FavoritesManager.IsFavorite(path);
                icon.Icon = active ? "IconHeartFilled" : "IconHeart";
                break;
            case "Repeat":
                string mode = _host.CurrentRepeatModeName;
                active = mode != "Off";
                icon.Icon = mode == "One" ? "IconRepeatOne" : "IconRepeatAll";
                break;
            default:
                active = _lyricsShown;
                icon.Icon = "IconLyrics";
                break;
        }

        icon.Foreground = active ? _accentBrush : DimBrush;
        dot.Fill = _accentBrush;
        dot.Opacity = function == "Repeat" && _host.CurrentIsShuffleEnabled ? 1 : 0;
    }

    private void OnFavoritesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshControlStates();

    private void OnRepeatModeChanged(string mode) => RefreshControlStates();

    private void OnShuffleStateChanged(bool enabled) => RefreshControlStates();

    private void SideButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        switch (ModeOf(button))
        {
            case "Favorite":
            {
                var anchor = HeartAnimation.Capture(button, this);
                _host.ExternalToggleFavoriteCurrentTrack();
                if (_host.CurrentTrackPath is { } path && FavoritesManager.IsFavorite(path))
                    HeartAnimation.Play(anchor, Settings);
                break;
            }
            case "Repeat":
                // После вытягивания кнопки отпускание мыши тоже даёт Click: он не должен менять повтор.
                if (_repeatClickSuppressed) _repeatClickSuppressed = false;
                else _host.ExternalToggleRepeat();
                break;
            default:
                // Выключение текста кнопкой убирает его и из свёрнутого острова.
                if (_lyricsShown) _compactLyricsOn = false;
                SetLyricsShown(!_lyricsShown);
                break;
        }
    }

    private void SideButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ModeOf((System.Windows.Controls.Button)sender) != "Repeat") return;

        e.Handled = true;
        _host.ExternalToggleShuffle();
    }

    private void SideButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _repeatDragStart = e.GetPosition(this).Y;
        _repeatClickSuppressed = false;
    }

    private void SideButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _repeatClickSuppressed) return;
        if (ModeOf((System.Windows.Controls.Button)sender) != "Repeat") return;
        if (Math.Abs(e.GetPosition(this).Y - _repeatDragStart) < RepeatDragDistance) return;

        _repeatClickSuppressed = true;
        _host.ExternalToggleShuffle();
    }

    // ----- Текст песни -----

    // При тексте остров раскрывается шире и заметно длиннее; из раскрытого вида выходим без текста.
    private void SetLyricsShown(bool shown)
    {
        if (_lyricsShown == shown) return;

        _lyricsShown = shown;
        var shape = ShapeOf(State.Open);
        OpenView.Width = shape.Width;
        OpenView.Height = shape.Height;
        _collapseTimer.Stop();
        RefreshControlStates();
        LyricsPanel.BeginAnimation(OpacityProperty, null);

        if (shown)
        {
            LyricsPanel.Visibility = Visibility.Visible;
            if (ReduceMotion) LyricsPanel.Opacity = 1;
            else
                LyricsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(160) });
            StartLyricsLoad();
        }
        else
        {
            CancelLyricsLoad();
            LyricsPanel.Visibility = Visibility.Collapsed;
            LyricsPanel.Opacity = 0;
        }

        if (_state != State.Open) return;

        // Закрытый текст возвращает обычное автосворачивание, если курсор уже ушёл.
        if (!shown && !_hovered && Settings.IslandAutoCollapse) StartCollapseTimer();
        BeginMorph();
        UpdateTargets();
    }

    private void StartLyricsLoad()
    {
        CancelLyricsLoad();
        _lyricsCts = new CancellationTokenSource();
        _lyricsPath = _host.CurrentTrackPath;
        _lyrics = LyricsDocument.Empty;
        LyricsVariantText.Text = "";
        ShowLyricsStatus("Ищем текст…", loading: true);
        BackgroundTask.FireAndForget(LoadLyricsAsync(_lyricsPath, _lyricsCts.Token), "IslandLyrics");
    }

    private void CancelLyricsLoad()
    {
        _lyricsCts?.Cancel();
        _lyricsCts = null;
    }

    // Тот же порядок, что в панели главного окна: локальный текст, затем автоподбор только при точном совпадении.
    private async Task LoadLyricsAsync(string? path, CancellationToken token)
    {
        try
        {
            LyricsDocument document = await LyricsService.LoadAsync(path, token);
            if (document.Kind == LyricsKind.None && !string.IsNullOrWhiteSpace(path))
            {
                var lookup = await LyricsService.FindAutomaticAsync(
                    _host.CurrentTitle, _host.CurrentArtist, _host.PlaybackState.Current.DurationSeconds, token);
                if (lookup.Best is { } exact)
                {
                    await LyricsService.SaveOnlineResultAsync(path, exact, token);
                    document = LyricsService.CreateDocumentFromOnlineResult(exact);

                    // Остальные совпадения запоминаем: если первым нашёлся простой текст, кнопка предложит синхронный.
                    ResetLyricsVariants(path);
                    _lyricsVariants.AddRange(lookup.Matches);
                    _lyricsVariantIndex = Math.Max(0, _lyricsVariants.IndexOf(exact));
                    _lyricsVariantsLrcLibLoaded = true;
                    _lyricsVariantsExtraLoaded = lookup.Matches.Any(match => match.Source != "LRCLIB");
                    UpdateLyricsVariantText();
                }
            }

            if (token.IsCancellationRequested) return;
            ShowLyrics(LyricsService.ApplyDisplayMode(document, Settings.LyricsDisplayMode));
        }
        catch (LyricsRateLimitException)
        {
            if (!token.IsCancellationRequested) ShowLyricsStatus("Поиск временно ограничен");
        }
        catch (OperationCanceledException)
        {
            // Нормально при смене трека или закрытии текста.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось загрузить текст для острова", ex);
            if (!token.IsCancellationRequested) ShowLyricsStatus("Текст не найден");
        }
    }

    // Пока текст ищется, вместо надписи бежит мерцание (если оно включено в настройках).
    private void ShowLyricsStatus(string text, bool loading = false)
    {
        LyricsLines.Visibility = LyricsScroll.Visibility = Visibility.Collapsed;
        bool shimmer = loading && Settings.IslandLyricsShimmer;
        LyricsShimmer.Visibility = shimmer ? Visibility.Visible : Visibility.Collapsed;
        LyricsStatus.Visibility = shimmer ? Visibility.Collapsed : Visibility.Visible;
        LyricsStatus.Text = LocalizationService.Translate(text);
    }

    private void ShowLyrics(LyricsDocument document)
    {
        bool synced = document.Kind == LyricsKind.Synced && document.Lines.Count > 0;
        bool plain = !synced && !string.IsNullOrWhiteSpace(document.PlainText);
        if (!synced && !plain)
        {
            ShowLyricsStatus("Текст не найден");
            return;
        }

        _lyrics = document;
        _lyricsLine = int.MinValue;
        if (synced && Settings.IslandSeekStyle == "Segments") SeekSegments.SetLines(document.Lines.Select(line => line.Time.TotalSeconds).ToArray());
        LyricsStatus.Visibility = LyricsShimmer.Visibility = Visibility.Collapsed;
        LyricsLines.Visibility = synced ? Visibility.Visible : Visibility.Collapsed;
        LyricsScroll.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        if (plain)
        {
            LyricsPlain.Text = document.PlainText.Trim();
            LyricsScroll.ScrollToTop();
        }
        else UpdateLyricLine(_host.PlaybackState.Current.PositionSeconds);

        if (_compactLyricsOn && _state == State.Compact) UpdateCompactLyric(_host.CurrentPlaybackSeconds);
    }

    // ----- Текст песни в свёрнутом острове -----

    private bool _compactLyricsOn;
    private double? _compactLyricsWidth;
    private const double CompactTextPadding = 41 * 2 + 14, CompactLyricsMaxWidth = 600;

    // Строка по текущему времени, а в паузах между строками и без текста — название трека.
    private string CompactText(double positionSeconds)
    {
        if (_compactLyricsOn && _lyrics.Kind == LyricsKind.Synced && _lyrics.Lines.Count > 0)
        {
            int active = LyricsService.FindActiveLineIndex(_lyrics.Lines, TimeSpan.FromSeconds(positionSeconds));
            if (active >= 0 && active < _lyrics.Lines.Count && !string.IsNullOrWhiteSpace(_lyrics.Lines[active].Text))
                return _lyrics.Lines[active].Text.Trim();
        }

        return _lastTitle;
    }

    private void UpdateCompactLyric(double positionSeconds, bool animate = true)
    {
        if (_state != State.Compact) return;

        string text = CompactText(positionSeconds);
        string shown = _pendingText.TryGetValue(CompactTitle, out string? pending) ? pending : CompactTitle.Text;
        // Строка сменяется мягко: старая гаснет, остров плавно меняет ширину, новая проявляется уже в нужной ширине.
        if (shown != text) SwapText(CompactTitle, text, animate && !ReduceMotion, default, () => FitCompactWidth(text), outMs: 150, inMs: 340);
        else FitCompactWidth(text);
    }

    // Свёрнутый остров расширяется и сужается под показанную строку; без текста песни ширина обычная.
    private void FitCompactWidth(string text)
    {
        double? width = null;
        if (_compactLyricsOn && text.Length > 0)
        {
            var typeface = new Typeface(CompactTitle.FontFamily, CompactTitle.FontStyle, CompactTitle.FontWeight, CompactTitle.FontStretch);
            var measured = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, CompactTitle.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            width = Math.Min(Math.Ceiling(measured.Width) + CompactTextPadding, CompactLyricsMaxWidth);
        }

        if (_compactLyricsWidth == width || (_compactLyricsWidth is { } old && width is { } now && Math.Abs(old - now) < 1)) return;

        _compactLyricsWidth = width;
        CompactView.Width = ShapeOf(State.Compact).Width;
        if (_state != State.Compact) return;

        // Ширина без перелёта: строка не должна «пружинить» на каждой смене.
        Tune(_width, 240, 31);
        BeginMorph();
        UpdateTargets();
    }

    // Границы сегментов полосы перемотки берутся из строк синхронного текста трека (только локальный, без онлайн-поиска).
    private void RefreshSeekLines()
    {
        if (Settings.IslandSeekStyle != "Segments") return;

        string? path = _host.CurrentTrackPath;
        if (string.Equals(path, _seekLinesPath, StringComparison.OrdinalIgnoreCase)) return;

        _seekLinesCts?.Cancel();
        _seekLinesPath = path;
        SeekSegments.SetLines([]);
        if (string.IsNullOrWhiteSpace(path)) return;

        _seekLinesCts = new CancellationTokenSource();
        BackgroundTask.FireAndForget(LoadSeekLinesAsync(path, _seekLinesCts.Token), "IslandSeekLines");
    }

    private async Task LoadSeekLinesAsync(string path, CancellationToken token)
    {
        try
        {
            LyricsDocument document = await LyricsService.LoadAsync(path, token);
            if (token.IsCancellationRequested || !string.Equals(path, _seekLinesPath, StringComparison.OrdinalIgnoreCase)) return;

            SeekSegments.SetLines(document.Kind == LyricsKind.Synced ? document.Lines.Select(line => line.Time.TotalSeconds).ToArray() : []);
        }
        catch (OperationCanceledException)
        {
            // Смена трека.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось прочитать строки текста для полосы перемотки острова", ex);
        }
    }

    // Колесо над простым текстом листает его; если листать нечего, оно по-прежнему меняет громкость острова.
    private void LyricsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (LyricsScroll.ScrollableHeight <= 0)
        {
            Island_MouseWheel(sender, e);
            return;
        }

        LyricsScroll.ScrollToVerticalOffset(Math.Clamp(LyricsScroll.VerticalOffset - e.Delta * 0.5, 0, LyricsScroll.ScrollableHeight));
    }

    private void ResetLyricsVariants(string? path)
    {
        _lyricsVariants.Clear();
        _lyricsVariantsPath = path;
        _lyricsVariantIndex = -1;
        _lyricsVariantsLrcLibLoaded = _lyricsVariantsExtraLoaded = false;
    }

    private void UpdateLyricsVariantText() =>
        LyricsVariantText.Text = _lyricsVariants.Count > 1 && _lyricsVariantIndex >= 0 ? $"{_lyricsVariantIndex + 1}/{_lyricsVariants.Count}" : "";

    private void LyricsVariantButton_Click(object sender, RoutedEventArgs e) =>
        BackgroundTask.FireAndForget(ShowNextLyricsVariantAsync(), nameof(LyricsVariantButton_Click));

    // Переключает трек на следующий найденный текст по кругу и сохраняет его так же, как окно Now Playing.
    private async Task ShowNextLyricsVariantAsync()
    {
        string? path = _host.CurrentTrackPath;
        if (_lyricsVariantBusy || path is null || string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
        if (Settings.LyricsSearchPolicy == "LocalOnly")
        {
            ShowLyricsStatus("Онлайн-поиск текста отключён");
            return;
        }

        if (!string.Equals(path, _lyricsVariantsPath, StringComparison.OrdinalIgnoreCase)) ResetLyricsVariants(path);

        CancelLyricsLoad();
        _lyricsCts = new CancellationTokenSource();
        var token = _lyricsCts.Token;
        _lyricsVariantBusy = true;
        LyricsVariantButton.IsEnabled = false;
        try
        {
            string title = _host.CurrentTitle, artist = _host.CurrentArtist;
            double duration = _host.PlaybackState.Current.DurationSeconds;

            void AddVariants(IReadOnlyList<OnlineLyricsResult> found)
            {
                foreach (var result in found)
                    if (!_lyricsVariants.Any(known => known.Source == result.Source && known.Id == result.Id)) _lyricsVariants.Add(result);
            }

            if (!_lyricsVariantsLrcLibLoaded)
            {
                ShowLyricsStatus("Ищем другие варианты…", loading: true);
                AddVariants(await LyricsService.FindLrcLibVariantsAsync(title, artist, duration, token));
                _lyricsVariantsLrcLibLoaded = true;
            }

            if (_lyricsVariantIndex + 1 >= _lyricsVariants.Count && !_lyricsVariantsExtraLoaded)
            {
                ShowLyricsStatus("Ищем другие варианты…", loading: true);
                AddVariants(await LyricsService.FindExtraVariantsAsync(title, artist, duration, token));
                _lyricsVariantsExtraLoaded = true;
            }

            if (token.IsCancellationRequested || !string.Equals(path, _host.CurrentTrackPath, StringComparison.OrdinalIgnoreCase)) return;

            if (_lyricsVariants.Count == 0 || (_lyricsVariants.Count == 1 && _lyricsVariantIndex == 0))
            {
                // Прежний текст остаётся на экране, если других нет.
                if (_lyrics.Kind == LyricsKind.None) ShowLyricsStatus(_lyricsVariants.Count == 0 ? "Подходящих вариантов не найдено" : "Других вариантов нет");
                else ShowLyrics(LyricsService.ApplyDisplayMode(_lyrics, Settings.LyricsDisplayMode));
                return;
            }

            int next = (_lyricsVariantIndex + 1) % _lyricsVariants.Count;
            await LyricsService.SaveOnlineResultAsync(path, _lyricsVariants[next], token);
            if (token.IsCancellationRequested || !string.Equals(path, _host.CurrentTrackPath, StringComparison.OrdinalIgnoreCase)) return;

            _lyricsVariantIndex = next;
            UpdateLyricsVariantText();
            ShowLyrics(LyricsService.ApplyDisplayMode(LyricsService.CreateDocumentFromOnlineResult(_lyricsVariants[next]), Settings.LyricsDisplayMode));
        }
        catch (LyricsRateLimitException)
        {
            if (!token.IsCancellationRequested) ShowLyricsStatus("Поиск временно ограничен");
        }
        catch (OperationCanceledException)
        {
            // Смена трека или закрытие текста.
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось сменить вариант текста в острове", ex);
            if (!token.IsCancellationRequested) ShowLyricsStatus("Не удалось сменить вариант текста");
        }
        finally
        {
            _lyricsVariantBusy = false;
            LyricsVariantButton.IsEnabled = true;
        }
    }

    private void UpdateLyricLine(double positionSeconds)
    {
        if (_lyrics.Kind != LyricsKind.Synced) return;

        int active = LyricsService.FindActiveLineIndex(_lyrics.Lines, TimeSpan.FromSeconds(positionSeconds));
        if (active == _lyricsLine) return;

        int previous = _lyricsLine;
        _lyricsLine = active;
        for (int i = 0; i < _lyricLines.Length; i++)
        {
            int index = active + i - 2;
            _lyricLines[i].Text = index >= 0 && index < _lyrics.Lines.Count ? _lyrics.Lines[index].Text : "";
        }

        ApplyLyricStyles();
        if (previous != int.MinValue && !ReduceMotion) AnimateLyricSlide(active - previous);
    }

    // Строки стоят на месте, меняется только текст в них: сдвиг на строку возвращает прежнюю картинку, и она плавно едет к новой.
    private void AnimateLyricSlide(int delta)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (Math.Abs(delta) <= 2)
            _lyricsShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta * LyricRowHeight, 0, LyricSlide) { EasingFunction = ease });

        for (int i = 0; i < _lyricLines.Length; i++)
        {
            // Строка, ставшая i-й, раньше стояла на месте i + delta.
            int was = i + delta;
            bool known = was >= 0 && was < _lyricLines.Length;
            var scale = (ScaleTransform)_lyricLines[i].RenderTransform;
            var fade = new DoubleAnimation(known ? LyricOpacity[was] : 0, LyricOpacity[i], LyricSlide) { EasingFunction = ease };
            var grow = new DoubleAnimation(known ? LyricScale[was] : LyricScale[0], LyricScale[i], LyricSlide) { EasingFunction = ease };
            _lyricLines[i].BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private void ApplyLyricStyles()
    {
        _lyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
        _lyricsShift.Y = 0;
        for (int i = 0; i < _lyricLines.Length; i++)
        {
            var scale = (ScaleTransform)_lyricLines[i].RenderTransform;
            _lyricLines[i].BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _lyricLines[i].Opacity = LyricOpacity[i];
            scale.ScaleX = scale.ScaleY = LyricScale[i];
        }
    }

    // ----- Эквалайзер -----

    // Полоски движутся только пока остров свёрнут и трек играет; в других видах их не видно.
    private void UpdateEqualizerState()
    {
        IslandBars.Active = _isPlaying && _state == State.Compact && Settings.IslandShowEqualizer && !ReduceMotion;
        OpenBars.Active = _isPlaying && _state == State.Open && Settings.IslandShowEqualizer && !ReduceMotion;
        if (ReduceMotion)
        {
            IslandBars.Reset();
            OpenBars.Reset();
        }

        IslandBackdropLayer.Active = _state == State.Open && !ReduceMotion;

        _host.SetIslandAudioLevelWanted(IslandBars.Active || OpenBars.Active || IslandBackdropLayer.Active);
    }

    // ----- Win32 -----

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
