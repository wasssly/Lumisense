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

    private enum State { Compact, Toast, Open, Volume }

    private const double HostWidth = 460;
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
    private const double PausedCoverScale = 0.85, CoverRadiusRatio = 0.22;
    private const double SkipMemorySeconds = 3;
    private const int EqualizerBarCount = 4;
    private const double VolumeStep = 0.02, AwayMargin = 30;
    private const double VolumeMax = 0.999, VolumeMin = 0.001, VolumeOvershoot = 7, VolumeOvershootSquash = 0.22, VolumeTrackWidth = 162;
    private static readonly TimeSpan OvershootHold = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan VolumeDuration = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KeepOnTopInterval = TimeSpan.FromSeconds(1);

    private static readonly Shape CompactShape = new(210, 34, 17);
    private static readonly Shape ToastShape = new(340, 68, 30);
    private static readonly Shape OpenShape = new(380, 176, 40);
    private static readonly Shape VolumeShape = new(250, 34, 17);
    private static readonly CoverSpot CompactCover = new(7, 6, 22);
    private static readonly CoverSpot ToastCover = new(12, 12, 44);
    private static readonly CoverSpot OpenCover = new(20, 20, 64);
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(550);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3.2);
    private static readonly TimeSpan CoverSlide = TimeSpan.FromMilliseconds(460);
    private static readonly TimeSpan FadeInDelay = TimeSpan.FromMilliseconds(70);
    private static readonly Color DefaultRim = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);

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
    private readonly System.Windows.Controls.Border[] _equalizerBars = new System.Windows.Controls.Border[EqualizerBarCount];
    private readonly DispatcherTimer _equalizerTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = CollapseDelay };
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
    private TrayMenuWindow? _menu;
    private bool _hovered, _pressed, _morphing, _isPlaying, _isSeeking, _loopRunning, _playbackShown;
    private Grab _grab;
    private Point _grabStart;
    private double _pull, _leanDrag, _lastRatio, _phase, _skipAt = -SkipMemorySeconds;
    private int _skipDirection = 1;
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
        _views = [CompactView, ToastView, VolumeView, OpenView];
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

        for (int i = 0; i < _equalizerBars.Length; i++)
        {
            // Высоту полоски меняем масштабом, а не Height: так анимация не вызывает перерасчёт раскладки.
            var bar = new System.Windows.Controls.Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new ScaleTransform(1, 0.25)
            };
            _equalizerBars[i] = bar;
            EqualizerPanel.Children.Add(bar);
        }

        _equalizerTimer.Tick += EqualizerTimer_Tick;
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_state == State.Open && !_hovered && _grab == Grab.None) SetState(State.Compact);
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
            ApplyShape();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionOnMonitor));

        _host.TrackInfoChanged += OnTrackInfoChanged;
        _host.PlaybackState.Changed += OnPlaybackChanged;
        _host.VolumeChanged += OnHostVolumeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += OnWindowClosed;
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
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _equalizerTimer.Stop();
        _collapseTimer.Stop();
        _toastTimer.Stop();
        _volumeTimer.Stop();
        _awayTimer.Stop();
        _overshootTimer.Stop();
        _keepTimer.Stop();
        _menu?.Close();
        StopLoop();
    }

    private AppSettings Settings => _host.Settings;

    private bool ReduceMotion => AccessibilityPreferences.ShouldReduceMotion(Settings) || _host.EffectiveGameOverlayCompatibilityEnabled;

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
        foreach (var bar in _equalizerBars) bar.Background = brush;
        UpdateRim();
    }

    // Применяет настройки острова на лету: масштаб, отступ, полоски, обводку и режим эффектов.
    public void ApplySettingsLive()
    {
        IslandOffset.Y = Math.Clamp(Settings.IslandTopGap, 0, 40) + _away.Value;
        UpdateAway(true);
        EqualizerPanel.Visibility = Settings.IslandShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
        if (!Settings.IslandTrackToast && _state == State.Toast) SetState(State.Compact);
        UpdateRim();
        UpdateEqualizerState();
        if (!_loopRunning) ApplyShape();
    }

    // Обводка берёт цвет акцента, пока что-то играет; иначе — нейтральная.
    private void UpdateRim()
    {
        var accent = _host.GetResolvedAccentColor();
        var target = Settings.IslandAccentRim && _isPlaying ? Color.FromArgb(0x8C, accent.R, accent.G, accent.B) : DefaultRim;
        if (ReduceMotion)
        {
            _rimBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _rimBrush.Color = target;
            return;
        }

        _rimBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(450)));
    }

    // ----- Состояния -----

    private static Shape ShapeOf(State state) => state switch
    {
        State.Toast => ToastShape,
        State.Open => OpenShape,
        State.Volume => VolumeShape,
        _ => CompactShape
    };

    private static CoverSpot SpotOf(State state) => state switch
    {
        State.Toast => ToastCover,
        State.Open => OpenCover,
        _ => CompactCover
    };

    private FrameworkElement ViewOf(State state) => state switch
    {
        State.Toast => ToastView,
        State.Open => OpenView,
        State.Volume => VolumeView,
        _ => CompactView
    };

    private void SetState(State next)
    {
        if (_state == next) return;

        var from = ShapeOf(_state);
        var to = ShapeOf(next);
        _state = next;

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
        UpdateEqualizerState();
        UpdateCoverScaleTarget();
        _toastTimer.Stop();
        _collapseTimer.Stop();
        _volumeTimer.Stop();
        if (next == State.Toast) _toastTimer.Start();
        else if (next == State.Open && !_hovered && Settings.IslandAutoCollapse) _collapseTimer.Start();
        UpdateTargets();
    }

    // Кеш в битмап на время анимации не используется: при его снятии текст перерисовывался с другим сглаживанием и «дёргался».
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
        double to = _state == State.Volume ? 0 : _state == State.Open && !_isPlaying ? PausedCoverScale : 1;
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
        IslandLean.X = _lean.Value;
        IslandOffset.Y = Math.Clamp(Settings.IslandTopGap, 0, 40) + _away.Value;

        double size = Math.Max(_coverSize.Value, 1);
        double radius = size * CoverRadiusRatio;
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
            if (view.Visibility != Visibility.Visible) continue;
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

        var shrink = new DoubleAnimation(FadeOutScale, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

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

        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { BeginTime = FadeInDelay, EasingFunction = ease };
        if (fresh) grow.From = FadeInScale;
        FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        FadeScaleOf(view).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { BeginTime = FadeInDelay });
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
        if (_state == State.Open && Settings.IslandAutoCollapse) _collapseTimer.Start();
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

        // Вытягивать вниз имеет смысл только свёрнутый остров; открытый на вытягивание не реагирует.
        if (_grab == Grab.Pull) _pull = _state == State.Open ? 0 : Math.Max(moved.Y, 0) / UserScale;
        else _leanDrag = moved.X / UserScale;
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
                SetState(_state == State.Open ? State.Compact : State.Open);
                break;
            case Grab.Pull when pulled >= PullToOpen && _state != State.Open:
                SetState(State.Open);
                break;
            case Grab.Lean when Math.Abs(leant) >= LeanToSkip:
                Skip(leant > 0 ? 1 : -1);
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

    // Направление последнего переключения запоминается на пару секунд: по нему обложка выезжает с нужной стороны.
    private void Skip(int direction)
    {
        _skipDirection = direction;
        _skipAt = _clock.Elapsed.TotalSeconds;
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
        SwapText(CompactTitle, title, live && _state == State.Compact, default);
        SwapText(ToastTitle, title, live && _state == State.Toast, default);
        SwapText(ToastArtist, artist, live && _state == State.Toast, TimeSpan.FromMilliseconds(45));
        SwapText(OpenTitle, title, live && _state == State.Open, default);
        SwapText(OpenArtist, artist, live && _state == State.Open, TimeSpan.FromMilliseconds(45));

        if (!ReferenceEquals(art, _shownArt) || _coverLayer is null)
        {
            _shownArt = art;
            int heading = _clock.Elapsed.TotalSeconds - _skipAt < SkipMemorySeconds ? _skipDirection : 1;
            ShowCover(art, heading, animate: announce && !ReduceMotion);
        }

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
    private void SwapText(TextBlock block, string text, bool animate, TimeSpan delay)
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
            return;
        }

        if (block.Text == text) return;

        _pendingText[block] = text;
        var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        move.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-5, TimeSpan.FromMilliseconds(120)) { BeginTime = delay, EasingFunction = easeIn });

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { BeginTime = delay, EasingFunction = easeIn };
        fade.Completed += (_, _) =>
        {
            if (!_pendingText.Remove(block, out string? latest)) return;

            block.Text = latest;
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = easeOut });
            block.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = easeOut });
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
        }

        // Пока остров не раскрыт, элементы плеера не видны: обновлять их на каждый снимок значит зря гонять раскладку.
        if (_state != State.Open || _isSeeking) return;

        _lastRatio = snapshot.ProgressRatio;
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

    private void UpdateSeekFill(double ratio) => SeekFill.Width = Math.Max(0, SeekHost.ActualWidth * Math.Clamp(ratio, 0, 1));

    private void SeekHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSeekFill(_lastRatio);

    // ----- Перемотка -----

    private double RatioFromMouse(MouseEventArgs e) =>
        SeekHost.ActualWidth <= 0 ? 0 : Math.Clamp(e.GetPosition(SeekHost).X / SeekHost.ActualWidth, 0, 1);

    private void SeekHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
        SeekHost.CaptureMouse();
        _lastRatio = RatioFromMouse(e);
        UpdateSeekFill(_lastRatio);
        e.Handled = true;
    }

    private void SeekHost_MouseMove(object sender, MouseEventArgs e)
    {
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
        double target = IsAway ? -(OpenShape.Height * UserScale + gap + AwayMargin) : 0;
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
        if (!Settings.IslandExternalVolume || IsAway || !IsLoaded || _clock.Elapsed - _loadedAt < StartupQuiet) return;

        ShowVolume(level);
    }

    private void ShowVolume(double level)
    {
        level = Math.Clamp(level, 0, 1);
        string percent = ((int)Math.Round(level * 100)).ToString();
        string icon = level <= 0 ? "IconSpeakerMute" : level < 0.5 ? "IconSpeaker" : "IconSpeaker2";

        // В раскрытом плеере форма не меняется: рядом с кнопками на пару секунд появляется маленький индикатор.
        if (_state == State.Open)
        {
            OpenVolumeText.Text = percent;
            OpenVolumeIcon.Icon = icon;
            var show = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1900) };
            show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
            show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
            OpenVolume.BeginAnimation(OpacityProperty, show);
            return;
        }

        bool entering = _state != State.Volume;
        VolumeText.Text = percent;
        VolumeIcon.Icon = icon;

        if (entering || ReduceMotion)
        {
            VolumeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            VolumeScale.ScaleX = level;
        }
        else
        {
            VolumeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(level, TimeSpan.FromMilliseconds(140))
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

    // Меню в том же окне, что и у значка в трее: оно выезжает от края экрана одним плавным движением.
    private void Island_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _menu?.Close();

        string nowPlaying = string.IsNullOrWhiteSpace(OpenArtist.Text) ? _lastTitle : $"{_lastTitle} — {OpenArtist.Text}";
        var entries = new List<TrayMenuEntry> { new(TrayMenuEntryKind.Header, "Lumisense", "IconMusicNote") };
        if (nowPlaying.Length > 0) entries.Add(new(TrayMenuEntryKind.Label, nowPlaying));
        entries.Add(new(TrayMenuEntryKind.Separator));
        entries.Add(new(TrayMenuEntryKind.Item, LocalizationService.Translate(_isPlaying ? "Пауза" : "Продолжить"),
            _isPlaying ? "IconPause" : "IconPlay", Click: _host.ExternalPlayPause));
        entries.Add(new(TrayMenuEntryKind.Item, LocalizationService.Translate("Следующий трек"), "IconNext", Click: () => Skip(1)));
        entries.Add(new(TrayMenuEntryKind.Item, LocalizationService.Translate("Предыдущий трек"), "IconPrevious", Click: () => Skip(-1)));
        entries.Add(new(TrayMenuEntryKind.Separator));
        entries.Add(new(TrayMenuEntryKind.Item, LocalizationService.Translate("Скрыть остров"), "IconMinimizePlayer", Click: HideTemporarily));
        entries.Add(new(TrayMenuEntryKind.Item, LocalizationService.Translate("Развернуть плеер"), "IconWindow", Click: () => _host.ExitMiniMode()));
        entries.Add(new(TrayMenuEntryKind.Item, LocalizationService.Translate("Настройки острова"), "IconSettings",
            Click: () => _host.ShowSettingsWindow("MiniPlayer")));

        var menu = new TrayMenuWindow(entries, !ReduceMotion);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };
        _menu = menu;
        var dpi = VisualTreeHelper.GetDpi(this);
        int inset = (int)Math.Round((Math.Clamp(Settings.IslandTopGap, 0, 40) + CompactShape.Height * UserScale + 8) * dpi.DpiScaleY);
        menu.ShowAtCursor(inset);
    }

    // ----- Эквалайзер -----

    // Полоски крутятся только пока остров свёрнут и трек играет: в других видах их не видно.
    private void UpdateEqualizerState()
    {
        bool animate = _isPlaying && _state == State.Compact && Settings.IslandShowEqualizer && !ReduceMotion;
        if (animate)
        {
            _equalizerTimer.Start();
            return;
        }

        _equalizerTimer.Stop();
        foreach (var bar in _equalizerBars) ((ScaleTransform)bar.RenderTransform).ScaleY = 0.25;
    }

    private void EqualizerTimer_Tick(object? sender, EventArgs e)
    {
        _phase += 0.35;
        for (int i = 0; i < _equalizerBars.Length; i++)
            ((ScaleTransform)_equalizerBars[i].RenderTransform).ScaleY = 0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(_phase * (1 + i * 0.37) + i * 1.7));
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
