using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace Lumisense;

public partial class NowPlayingWindow : Window
{
    private readonly record struct PalettePoint(double Red, double Green, double Blue, double Weight);

    private readonly INowPlayingHost _owner;
    private readonly ObservableCollection<LyricLine> _syncedLines = new();
    private readonly ObservableCollection<OnlineLyricsResult> _onlineResults = new();
    private CancellationTokenSource? _lyricsLoadCts;
    private CancellationTokenSource? _onlineSearchCts;

    // Варианты текста для кнопки «Другой вариант»: уверенные совпадения по убыванию качества для текущего трека.
    // LRCLIB и дополнительные источники подгружаются по очереди, только когда варианты закончились.
    private readonly List<OnlineLyricsResult> _lyricsVariants = new();
    private string? _lyricsVariantsTrackPath;
    private int _lyricsVariantIndex = -1;
    private bool _lyricsVariantsLrcLibLoaded;
    private bool _lyricsVariantsExtraLoaded;
    private bool _lyricsVariantBusy;
    private DispatcherTimer? _ambientMotionTimer;
    private double _ambientMotionTime;
    private double _ambientSpeed = 1.0;
    private double _ambientTargetSpeed = 1.0;

    // Ниже этой скорости на паузе облака считаются остановившимися: таймер выключается, чтобы окно, открытое часами
    // на втором мониторе, не нагружало UI-поток. Сцена хотя бы раз рисуется тиком (_ambientScenePlaced).
    private const double AmbientIdleSpeedThreshold = 0.003;
    private bool _ambientScenePlaced;
    private long _ambientLastTimestamp;
    private bool _lyricsPanelVisible = true;
    private LyricsDocument _lyrics = LyricsDocument.Empty;
    // Документ как он загружен; _lyrics — то, что показано с учётом AppSettings.LyricsDisplayMode.
    private LyricsDocument _lyricsSource = LyricsDocument.Empty;
    private string? _lyricsTrackPath;
    private int _activeLyricIndex = -2;

    internal NowPlayingWindow(INowPlayingHost owner)
    {
        _owner = owner;
        InitializeComponent();
        AccessibilityPreferences.ApplyToWindow(this, _owner.Settings);

        SyncedLyricsList.ItemsSource = _syncedLines;
        NowPlayingInfoPanel.SizeChanged += (_, _) => UpdateInfoPanelOffset();
        // Запас под последней строкой зависит от высоты списка; при смене размера окна пересчитываем его.
        SyncedLyricsList.SizeChanged += (_, _) => UpdateLyricsEndPadding();
        ApplyLyricsAppearance();
        ApplyBackgroundMode();
        SizeChanged += (_, _) => UpdateBackdropGeometry();
        OnlineLyricsResultsList.ItemsSource = _onlineResults;
        Loaded += NowPlayingWindow_Loaded;
        Closed += NowPlayingWindow_Closed;
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
    }

    private void NowPlayingWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _owner.TrackInfoChanged += Owner_TrackInfoChanged;
        _owner.PlaybackState.Changed += Owner_PlaybackSnapshotChanged;

        InitializeAmbientAnimation();
        ApplyProgressBarStyle();
        RefreshTrackPresentation();
        Owner_PlaybackSnapshotChanged(_owner.PlaybackState.Current);
        UpdateLyricsLayout();
    }

    // Вид полосы (AppSettings.ProgressBarStyle): Material и MaterialSlider рисуют свои элементы, всё остальное (включая Waveform) — обычная полоса.
    public void ApplyProgressBarStyle()
    {
        string style = ProgressStyleResolver.Effective(_owner.Settings, ProgressSurface.NowPlaying);
        bool wave = style == "Material";
        bool slider = style == "MaterialSlider";

        ArtworkProgressBar.Visibility = wave || slider ? Visibility.Hidden : Visibility.Visible;
        ArtworkProgressMaterial.Visibility = wave ? Visibility.Visible : Visibility.Collapsed;
        ArtworkProgressMaterialSlider.Visibility = slider ? Visibility.Visible : Visibility.Collapsed;

        ApplyAccentColor();
        ArtworkProgressMaterial.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.Progress);
        ArtworkProgressMaterial.IsWaving = _owner.IsPlayingNow;
    }

    // Эти кисти заданы значением, а не DynamicResource: при смене акцента (в том числе от обложки на каждом треке)
    // их нужно переназначать явно.
    public void ApplyAccentColor()
    {
        var accent = new SolidColorBrush(_owner.GetResolvedAccentColor());
        accent.Freeze();
        ArtworkProgressMaterial.PlayedBrush = accent;
        ArtworkProgressMaterialSlider.ActiveBrush = accent;
        ArtworkProgressBar.Foreground = accent;
        // Primary-кнопка WPF-UI берёт цвет из темы, а не из подменяемых ресурсов, поэтому красим её явно, как в главном окне.
        ArtworkPlayPauseButton.Background = accent;
        ArtworkPlayPauseIcon.Foreground = IconResources.AccentContrastBrush;
        ApplyProgressAppearance();
    }

    // Профиль вида полосы для Now Playing (ProgressBarAppearance): цвет и размеры поверх акцента, выставленного выше.
    public void ApplyProgressAppearance()
    {
        ProgressBarAppearance appearance = ProgressAppearanceApplier.For(_owner.Settings, ProgressSurface.NowPlaying);
        SolidColorBrush brush = ProgressAppearanceApplier.ToBrush(_owner.GetProgressBarColor(ProgressSurface.NowPlaying));

        ProgressAppearanceApplier.Apply(ArtworkProgressMaterial, appearance);
        ProgressAppearanceApplier.Apply(ArtworkProgressMaterialSlider, appearance);
        ArtworkProgressMaterial.PlayedBrush = brush;
        ArtworkProgressMaterialSlider.ActiveBrush = brush;
        ArtworkProgressBar.Foreground = brush;
        ArtworkProgressBar.Height = 8.0 * appearance.TrackThickness;
    }

    // У ScrollViewer нет анимируемого свойства VerticalOffset: attached-свойство проксирует анимацию в ScrollToVerticalOffset.
    private static readonly DependencyProperty AnimatedScrollOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedScrollOffset", typeof(double), typeof(NowPlayingWindow),
        new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is ScrollViewer viewer && e.NewValue is double offset) viewer.ScrollToVerticalOffset(offset);
        }));

    private UIElement? _glowElement;

    // Активная строка стоит по вертикали в центре списка, а не у нижнего края, как после ScrollIntoView.
    private void SmoothScrollLyricsToLine(LyricLine line)
    {
        // CanContentScroll=False: смещение, высота области и контейнера — в одних единицах (пикселях).
        SyncedLyricsList.UpdateLayout();
        if (UpdateLyricsEndPadding()) SyncedLyricsList.UpdateLayout();
        var container = SyncedLyricsList.ItemContainerGenerator.ContainerFromItem(line) as FrameworkElement;
        if (container is null)
        {
            SyncedLyricsList.ScrollIntoView(line);
            SyncedLyricsList.UpdateLayout();
            container = SyncedLyricsList.ItemContainerGenerator.ContainerFromItem(line) as FrameworkElement;
            if (container is null) return;
        }

        ScrollViewer? viewer = FindScrollViewer(SyncedLyricsList);
        if (viewer is null || viewer.ViewportHeight <= 0 || container.ActualHeight <= 0) return;

        try
        {
            Point itemTop = container.TranslatePoint(new Point(0, 0), SyncedLyricsList);
            double viewportHeight = Math.Min(viewer.ViewportHeight, SyncedLyricsList.ActualHeight);
            double maxOffset = Math.Max(0, viewer.ExtentHeight - viewer.ViewportHeight);
            // Целое число пикселей: при нечётной разности высот центр даёт смещение x,5, и строка останавливалась между
            // пикселями. Тогда её картинка с эффектами (свечение, размытие), которая привязана к целым пикселям, и обычный
            // текст стояли на полпикселя по-разному, а при смене способа отрисовки текст «дёргался».
            double target = Math.Clamp(Math.Round(
                viewer.VerticalOffset + itemTop.Y - (viewportHeight - container.ActualHeight) / 2), 0, Math.Floor(maxOffset));

            if (AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.Lyrics))
            {
                viewer.BeginAnimation(AnimatedScrollOffsetProperty, null);
                viewer.ScrollToVerticalOffset(target);
                return;
            }

            var animation = new DoubleAnimation(viewer.VerticalOffset, target, new Duration(TimeSpan.FromMilliseconds(520)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            viewer.BeginAnimation(AnimatedScrollOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        catch (InvalidOperationException)
        {
            // Во время пересоздания контейнеров ListBox visual tree может быть временно разорван.
        }
    }

    // Запас под последней строкой: без него у конца трека список упирается в низ и последние строки «съезжают» вниз,
    // а с ним их можно поднять к середине области (не выше: остаётся запас на половину высоты строки).
    private bool UpdateLyricsEndPadding()
    {
        if (FindItemsPanel(SyncedLyricsList) is not { } panel) return false;

        double bottom = Math.Max(0, SyncedLyricsList.ActualHeight / 2 - 28);
        if (Math.Abs(panel.Margin.Bottom - bottom) < 0.5) return false;

        panel.Margin = new Thickness(0, 0, 0, bottom);
        return true;
    }

    private static Panel? FindItemsPanel(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is Panel { IsItemsHost: true } panel) return panel;
            if (FindItemsPanel(child) is { } nested) return nested;
        }

        return null;
    }

    // Мягкое свечение только у текста активной строки (DropShadowEffect на каждой строке был бы дорог в длинных текстах);
    // эффект на самом TextBlock, а не на ListBoxItem, иначе светился бы весь скруглённый фон выделения.
    private void RefreshActiveLyricGlow()
    {
        UIElement? wanted = null;
        if (LyricsHighlightStyle.HasGlow(_owner.Settings.SyncedLyricsHighlightEffect) &&
            _activeLyricIndex >= 0 && _activeLyricIndex < _syncedLines.Count &&
            SyncedLyricsList.ItemContainerGenerator.ContainerFromItem(_syncedLines[_activeLyricIndex]) is DependencyObject container)
        {
            wanted = FindDescendant<TextBlock>(container);
        }

        int glowStrength = _owner.Settings.SyncedLyricsGlowStrength;
        double glowRadius = LyricsHighlightStyle.GlowBlurRadius(glowStrength);
        double glowOpacity = LyricsHighlightStyle.GlowOpacity(glowStrength);

        // Повторный вызов для той же строки не пересоздаёт эффект (это мигало бы свечением), а лишь подстраивает силу.
        if (wanted is not null && ReferenceEquals(wanted, _glowElement) &&
            wanted.Effect is System.Windows.Media.Effects.DropShadowEffect current)
        {
            current.BlurRadius = glowRadius;
            current.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(glowOpacity, TimeSpan.FromMilliseconds(150)));
            return;
        }

        if (_glowElement is not null) FadeOutGlow(_glowElement);
        _glowElement = null;
        if (wanted is null) return;

        // Свечение проявляется плавно: мгновенное появление эффекта меняло отрисовку текста и давало «подёргивание».
        var effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.White,
            BlurRadius = glowRadius,
            ShadowDepth = 0,
            Opacity = 0,
        };
        wanted.Effect = effect;
        bool reduceMotion = AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.Lyrics);
        if (reduceMotion)
            effect.Opacity = glowOpacity;
        else
            effect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(glowOpacity, TimeSpan.FromMilliseconds(300)));
        _glowElement = wanted;
    }

    private static void FadeOutGlow(UIElement element)
    {
        if (element.Effect is not System.Windows.Media.Effects.DropShadowEffect effect) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        // Если за это время на элемент уже повесили новый эффект, старый трогать нельзя.
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(element.Effect, effect)) element.Effect = null;
        };
        effect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, fade);
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    // Вид текста песни: выравнивание (AppSettings.LyricsTextAlignment) и свечение активной строки
    // (AppSettings.SyncedLyricsHighlightEffect). Вызывается при создании окна и из MainWindow при смене настроек.
    internal void ApplyLyricsAppearance()
    {
        double fontSize = Math.Clamp(_owner.Settings.NowPlayingLyricsFontSize,
            LyricsHighlightStyle.MinNowPlayingFontSize, LyricsHighlightStyle.MaxNowPlayingFontSize);
        bool fontChanged = Math.Abs(SyncedLyricsList.FontSize - fontSize) > 0.01;
        SyncedLyricsList.FontSize = fontSize;
        PlainLyricsText.FontSize = fontSize - 1;
        PlainLyricsText.LineHeight = Math.Round((fontSize - 1) * 1.65);
        RefreshActiveLyricGlow();
        ApplyLyricLineStates(0, _syncedLines.Count - 1, animate: false);
        TextAlignment alignment = LyricsTextAlignmentMode.ToTextAlignment(_owner.Settings.LyricsTextAlignment);
        SyncedLyricsList.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, alignment);
        PlainLyricsText.TextAlignment = alignment;

        // Смена размера перекомпоновывает строки, поэтому активную строку возвращаем в центр уже после layout.
        if (fontChanged) RecenterActiveLyricLater();
    }

    public void ApplyAccessibilityPreferences()
    {
        AccessibilityPreferences.ApplyToWindow(this, _owner.Settings);
        ApplyProgressBarStyle();

        if (AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.NowPlaying))
        {
            _ambientSpeed = 0;
            _ambientTargetSpeed = 0;
            _ambientMotionTimer?.Stop();
            UpdateDynamicDriver(false);
            return;
        }

        UpdateAmbientAnimation(_owner.IsPlayingNow);
    }

    private void NowPlayingWindow_Closed(object? sender, EventArgs e)
    {
        _owner.TrackInfoChanged -= Owner_TrackInfoChanged;
        _owner.PlaybackState.Changed -= Owner_PlaybackSnapshotChanged;
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        CancelLyricsLoad();
        CancelOnlineSearch();
        StopDynamicDriver();
        _owner.SetAudioLevelMeterEnabled(false);
        if (_ambientMotionTimer is not null)
        {
            _ambientMotionTimer.Stop();
            _ambientMotionTimer.Tick -= AmbientMotionTimer_Tick;
            _ambientMotionTimer = null;
        }
    }

    private void Owner_TrackInfoChanged(string title, string artist, System.Windows.Media.Brush? _) => RefreshTrackPresentation();

    private void Owner_PlaybackSnapshotChanged(PlaybackSnapshot snapshot)
    {
        UpdatePlaybackState(snapshot.IsPlaying);
        UpdateProgress(snapshot.PositionSeconds, snapshot.DurationSeconds);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;

        // Заголовок, источник текста и подсказка play/pause формируются программно,
        // поэтому их нужно обновить отдельно от обхода статического visual tree.
        ApplyLyricsDocument(_lyrics);
        Owner_PlaybackSnapshotChanged(_owner.PlaybackState.Current);
    }

    // Размеры обложки, колонок и панели текста заданы в «логических» пикселях под экран не меньше DesignWidth × DesignHeight.
    // Если окно меньше (1366×768, 1280×720, большой масштаб Windows), контент уменьшается целиком, а все расчёты размеров
    // ведутся по LogicalWidth/LogicalHeight — размеру, который контент видит внутри уменьшения.
    private const double DesignWidth = 1450;
    private const double DesignHeight = 940;
    private const double MinContentScale = 0.5;
    private double _contentScale = 1.0;

    private double LogicalWidth => ActualWidth / _contentScale;
    private double LogicalHeight => ActualHeight / _contentScale;

    private void UpdateContentScale()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double scale = Math.Round(Math.Clamp(Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight), MinContentScale, 1.0), 3);
        if (Math.Abs(scale - _contentScale) < 0.001) return;

        _contentScale = scale;
        NowPlayingContentRoot.LayoutTransform = scale >= 0.999 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    private void NowPlayingWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentScale();
        if (!_lyricsPanelVisible)
            UpdateLyricsLayout();
        if (_focusMode && !_focusModeAnimating)
            LyricsPanel.Height = GetLyricsPanelHeight();
    }

    // Режим «только название, обложка и текст». Скрываются управление над текстом, полоса прогресса под обложкой и кнопки
    // на обложке; панель текста вырастает по высоте, чтобы занять освободившееся место.
    private const double NormalLyricsPanelHeight = 620;
    private bool _focusMode;
    private bool _focusModeAnimating;

    private double GetLyricsPanelHeight() =>
        _focusMode ? Math.Clamp(LogicalHeight - 260, NormalLyricsPanelHeight, 900) : NormalLyricsPanelHeight;

    // В режиме без лишнего кнопки над обложкой не показываются; если панель текста скрыта, они единственный способ её вернуть.
    private bool ArtworkOverlayAllowed => !(_focusMode && _lyricsPanelVisible);

    // Прозрачный (Opacity=0) элемент в WPF остаётся кликабельным, поэтому скрытые кнопки нужно ещё и отключать для мыши.
    private void UpdateArtworkOverlayInteractivity()
    {
        // UpdateLyricsLayout может сработать, пока разметка ещё создаётся.
        if (ArtworkControlsOverlay is not null) ArtworkControlsOverlay.IsHitTestVisible = ArtworkOverlayAllowed;
    }

    private void FocusModeButton_Click(object sender, RoutedEventArgs e)
    {
        _focusMode = !_focusMode;
        ApplyFocusMode(animate: IsLoaded && !AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.NowPlaying));
    }

    private void ApplyFocusMode(bool animate)
    {
        bool hide = _focusMode;
        FocusModeButton.Opacity = hide ? 0.9 : 0.42;
        FocusModeButton.ToolTip = LocalizationService.Translate(hide ? "Показать панель управления" : "Скрыть панель управления и всё лишнее");

        LyricsControlsPanel.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        if (hide) AnimateArtworkControls(visible: false);
        UpdateArtworkOverlayInteractivity();
        SetProgressHidden(hide, animate);

        double target = GetLyricsPanelHeight();
        if (!animate)
        {
            LyricsPanel.BeginAnimation(HeightProperty, null);
            LyricsPanel.Height = target;
            RecenterActiveLyricLater();
            return;
        }

        _focusModeAnimating = true;
        var animation = new DoubleAnimation(LyricsPanel.ActualHeight > 0 ? LyricsPanel.ActualHeight : NormalLyricsPanelHeight, target,
            new Duration(TimeSpan.FromMilliseconds(360)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Completed += (_, _) =>
        {
            _focusModeAnimating = false;
            LyricsPanel.BeginAnimation(HeightProperty, null);
            LyricsPanel.Height = GetLyricsPanelHeight();
            RecenterActiveLyricLater();
        };
        LyricsPanel.BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // Полоса прогресса под обложкой плавно сворачивается по высоте (LayoutTransform), а не пропадает рывком: обложка и текст
    // центрируются друг относительно друга, и мгновенное исчезновение строки сдвинуло бы их скачком.
    private void SetProgressHidden(bool hide, bool animate)
    {
        UIElement wrap = ArtworkProgressWrap;
        if (!animate)
        {
            wrap.BeginAnimation(OpacityProperty, null);
            wrap.Opacity = 1;
            ArtworkProgressWrap.LayoutTransform = Transform.Identity;
            wrap.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        double from = wrap.Visibility != Visibility.Visible
            ? 0
            : ArtworkProgressWrap.LayoutTransform is ScaleTransform current ? current.ScaleY : 1;
        var scale = new ScaleTransform(1, Math.Max(from, 0.001));
        ArtworkProgressWrap.LayoutTransform = scale;
        wrap.Visibility = Visibility.Visible;

        var duration = new Duration(TimeSpan.FromMilliseconds(360));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var scaleAnimation = new DoubleAnimation(Math.Max(from, 0.001), hide ? 0.001 : 1, duration) { EasingFunction = easing };
        scaleAnimation.Completed += (_, _) =>
        {
            // Пока шла анимация, режим могли переключить обратно: завершаем только своё действие.
            if (hide && _focusMode)
                wrap.Visibility = Visibility.Collapsed;
            else if (!hide && !_focusMode)
                ArtworkProgressWrap.LayoutTransform = Transform.Identity;
        };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);

        double opacityFrom = wrap.Opacity;
        wrap.BeginAnimation(OpacityProperty, hide
            ? new DoubleAnimation(opacityFrom, 0, TimeSpan.FromMilliseconds(220))
            : new DoubleAnimation(opacityFrom, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(100) },
            HandoffBehavior.SnapshotAndReplace);
    }

    // После смены высоты списка активная строка снова встаёт по центру.
    private void RecenterActiveLyricLater()
    {
        int index = _activeLyricIndex;
        if (index < 0 || index >= _syncedLines.Count) return;

        LyricLine activeLine = _syncedLines[index];
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_activeLyricIndex == index) SmoothScrollLyricsToLine(activeLine);
        }));
    }

    // Название и исполнитель стоят над обложкой, не занимая места в раскладке: сдвигаем панель вверх на её высоту и зазор.
    private const double InfoPanelGap = 22;

    private void UpdateInfoPanelOffset()
    {
        if (NowPlayingInfoPanel.RenderTransform is TranslateTransform { IsFrozen: false } translate)
            translate.Y = -(NowPlayingInfoPanel.ActualHeight + InfoPanelGap);
    }

    private bool _lyricsLayoutAnimating;

    // Запуск через FireAndForget, а не async void: исключение анимации должно попасть в журнал, а не в Dispatcher.
    private void ToggleLyricsButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ToggleLyricsAsync(), nameof(ToggleLyricsButton_Click));

    private async Task ToggleLyricsAsync()
    {
        if (_lyricsLayoutAnimating) return;

        bool show = !_lyricsPanelVisible;
        if (!IsLoaded || AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.NowPlaying))
        {
            _lyricsPanelVisible = show;
            UpdateLyricsLayout();
            return;
        }

        _lyricsLayoutAnimating = true;
        try
        {
            await AnimateLyricsLayoutChangeAsync(show);
        }
        finally
        {
            if (_lyricsPanelVisible != show)
            {
                _lyricsPanelVisible = show;
                UpdateLyricsLayout();
            }
            ResetLyricsLayoutTransitionState();
            _lyricsLayoutAnimating = false;
        }
    }

    // Колонки сетки переключаются мгновенно, поэтому плавность даём приёмом «до/после»: текст и заголовок гаснут,
    // раскладка меняется, а обложка и полоса прогресса выезжают из прежних позиций и размеров к новым.
    private async Task AnimateLyricsLayoutChangeAsync(bool show)
    {
        var fadeOut = TimeSpan.FromMilliseconds(180);
        var move = new Duration(TimeSpan.FromMilliseconds(420));
        var easeInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        NowPlayingInfoPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, fadeOut) { EasingFunction = easeOut });
        if (!show)
        {
            var slide = new TranslateTransform();
            LyricsPanel.RenderTransform = slide;
            LyricsPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, fadeOut) { EasingFunction = easeOut });
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, fadeOut) { EasingFunction = easeOut });
        }
        await Task.Delay(fadeOut);

        Point surfaceBefore = ArtworkSurface.TranslatePoint(new Point(), this);
        double surfaceWidthBefore = ArtworkSurface.ActualWidth;
        Point progressBefore = ArtworkProgressGrid.TranslatePoint(new Point(), this);
        double progressWidthBefore = ArtworkProgressGrid.ActualWidth;

        _lyricsPanelVisible = show;
        UpdateLyricsLayout();
        LyricsPanel.BeginAnimation(UIElement.OpacityProperty, null);
        LyricsPanel.Opacity = 0;
        NowPlayingInfoPanel.BeginAnimation(UIElement.OpacityProperty, null);
        NowPlayingInfoPanel.Opacity = 0;
        UpdateLayout();

        StartLayoutMove(ArtworkSurface, surfaceBefore, surfaceWidthBefore, move, easeInOut);
        StartLayoutMove(ArtworkProgressGrid, progressBefore, progressWidthBefore, move, easeInOut);

        NowPlayingInfoPanel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(140), EasingFunction = easeOut });

        if (show)
        {
            var slideIn = new TranslateTransform(24, 0);
            LyricsPanel.RenderTransform = slideIn;
            var delay = TimeSpan.FromMilliseconds(180);
            var duration = TimeSpan.FromMilliseconds(340);
            LyricsPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, duration) { BeginTime = delay, EasingFunction = easeOut });
            slideIn.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, duration) { BeginTime = delay, EasingFunction = easeOut });
            await Task.Delay(delay + duration + TimeSpan.FromMilliseconds(40));
            return;
        }

        await Task.Delay(move.TimeSpan + TimeSpan.FromMilliseconds(40));
    }

    private void StartLayoutMove(FrameworkElement element, Point before, double widthBefore, Duration duration, IEasingFunction easing)
    {
        // Скрытый (в том числе через родителя) элемент двигать незачем.
        if (!element.IsVisible) return;

        Point after = element.TranslatePoint(new Point(), this);
        double widthAfter = element.ActualWidth;
        double scale = widthAfter > 0 && widthBefore > 0 ? widthBefore / widthAfter : 1.0;

        var scaleTransform = new ScaleTransform(scale, scale);
        var translate = new TranslateTransform(before.X - after.X, before.Y - after.Y);
        element.RenderTransformOrigin = new Point(0, 0);
        element.RenderTransform = new TransformGroup { Children = { scaleTransform, translate } };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
    }

    private void ResetLyricsLayoutTransitionState()
    {
        // Трансформ панели с названием — это её постоянное положение над обложкой, его здесь не сбрасываем.
        NowPlayingInfoPanel.BeginAnimation(UIElement.OpacityProperty, null);
        NowPlayingInfoPanel.Opacity = 1;
        LyricsPanel.BeginAnimation(UIElement.OpacityProperty, null);
        LyricsPanel.Opacity = 1;
        LyricsPanel.RenderTransform = Transform.Identity;

        foreach (FrameworkElement element in new FrameworkElement[] { ArtworkSurface, ArtworkProgressGrid })
        {
            element.RenderTransform = Transform.Identity;
            element.RenderTransformOrigin = new Point(0, 0);
        }
    }

    private void UpdateLyricsLayout()
    {
        LyricsPanel.Visibility = _lyricsPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        ShowLyricsOnArtworkButton.Visibility = _lyricsPanelVisible ? Visibility.Collapsed : Visibility.Visible;
        UpdateArtworkOverlayInteractivity();

        if (_lyricsPanelVisible)
        {
            ArtworkColumn.Width = new GridLength(560);
            ArtworkGapColumn.Width = new GridLength(52);
            LyricsColumn.Width = new GridLength(1, GridUnitType.Star);
            NowPlayingTitleText.HorizontalAlignment = HorizontalAlignment.Stretch;
            NowPlayingTitleText.TextAlignment = TextAlignment.Left;
            NowPlayingArtistText.HorizontalAlignment = HorizontalAlignment.Stretch;
            NowPlayingArtistText.TextAlignment = TextAlignment.Left;
            SetArtworkSize(540);
            return;
        }

        ArtworkColumn.Width = new GridLength(1, GridUnitType.Star);
        ArtworkGapColumn.Width = new GridLength(0);
        LyricsColumn.Width = new GridLength(0);
        NowPlayingTitleText.HorizontalAlignment = HorizontalAlignment.Center;
        NowPlayingTitleText.TextAlignment = TextAlignment.Center;
        NowPlayingArtistText.HorizontalAlignment = HorizontalAlignment.Center;
        NowPlayingArtistText.TextAlignment = TextAlignment.Center;

        double availableWidth = Math.Max(540, Math.Min(LogicalWidth - 140, 780));
        // Над обложкой висят название и исполнитель (около 170 px), под ней полоса прогресса: на них оставляем место.
        double availableHeight = Math.Max(540, LogicalHeight - 400);
        SetArtworkSize(Math.Min(availableWidth, availableHeight));
    }

    private void SetArtworkSize(double size)
    {
        double safeSize = Math.Max(540, Math.Min(size, 780));
        ArtworkSurface.Width = safeSize;
        ArtworkSurface.Height = safeSize;
        ArtworkProgressGrid.Width = safeSize;
        NowPlayingInfoPanel.Width = safeSize;
        ArtworkImage.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, safeSize - 2), Math.Max(1, safeSize - 2)), 22, 22);
    }

    private void RefreshTrackPresentation()
    {
        NowPlayingTitleText.Text = _owner.CurrentTitle;
        NowPlayingArtistText.Text = _owner.CurrentArtist;
        ApplyArtwork(_owner.CurrentAlbumArt);

        string? path = _owner.CurrentTrackPath;
        if (!string.Equals(path, _lyricsTrackPath, StringComparison.OrdinalIgnoreCase))
        {
            CancelOnlineSearch();
            HideOnlineLyricsSearch();
            _ = LoadLyricsAsync(path);
        }
    }

    private void ApplyArtwork(BitmapImage? artwork)
    {
        ArtworkImage.Source = artwork;
        ArtworkImage.Visibility = artwork is null ? Visibility.Collapsed : Visibility.Visible;
        ArtworkPlaceholder.Visibility = artwork is null ? Visibility.Visible : Visibility.Collapsed;
        ApplyArtworkPalette(artwork);
    }

    private void ApplyArtworkPalette(BitmapSource? artwork)
    {
        // Цвета шаров и волн — пять RGB-кластеров обложки; облака берут больше цветов (см. CloudFlow).
        System.Windows.Media.Color[] cloudColors = artwork is null ? CreateFallbackPalette() : ExtractArtworkPalette(artwork);

        // Облака: до восьми цветов обложки с весами по площади; цвета плавно перетекают при смене трека.
        PalettePoint[] clusters = artwork is null
            ? FallbackClusters(CloudPointCount)
            : ExtractArtworkClusters(artwork, CloudPointCount);
        SetCloudPalette(clusters, instant: !IsLoaded || AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.NowPlaying));

        ApplyDynamicPalette(cloudColors, animate: true);
    }

    private static System.Windows.Media.Color[] ExtractArtworkPalette(BitmapSource artwork) =>
        ExtractArtworkClusters(artwork, 5).Select(ToColor).ToArray();

    // Кластеры цветов обложки (k-means по RGB) по убыванию веса: вес — доля площади, занятая кластером.
    private static PalettePoint[] ExtractArtworkClusters(BitmapSource artwork, int count)
    {
        try
        {
            double scale = Math.Min(1.0, 128.0 / Math.Max(artwork.PixelWidth, artwork.PixelHeight));
            var small = new TransformedBitmap(artwork, new System.Windows.Media.ScaleTransform(scale, scale));
            var pixels = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int width = pixels.PixelWidth;
            int height = pixels.PixelHeight;
            if (width <= 0 || height <= 0) return FallbackClusters(count);

            int stride = width * 4;
            byte[] buffer = new byte[stride * height];
            pixels.CopyPixels(buffer, stride, 0);
            var samples = new List<PalettePoint>();
            for (int index = 0; index + 3 < buffer.Length; index += 12)
            {
                byte alpha = buffer[index + 3];
                if (alpha < 32) continue;
                samples.Add(new PalettePoint(buffer[index + 2], buffer[index + 1], buffer[index], alpha / 255.0));
            }
            if (samples.Count == 0) return FallbackClusters(count);

            const int colorStep = 32;
            var buckets = new Dictionary<int, PalettePoint>();
            foreach (PalettePoint sample in samples)
            {
                int key = ((int)sample.Red / colorStep << 16) | ((int)sample.Green / colorStep << 8) | (int)sample.Blue / colorStep;
                if (buckets.TryGetValue(key, out PalettePoint bucket))
                    buckets[key] = new PalettePoint(bucket.Red + sample.Red * sample.Weight, bucket.Green + sample.Green * sample.Weight,
                        bucket.Blue + sample.Blue * sample.Weight, bucket.Weight + sample.Weight);
                else
                    buckets[key] = new PalettePoint(sample.Red * sample.Weight, sample.Green * sample.Weight,
                        sample.Blue * sample.Weight, sample.Weight);
            }

            var seeds = buckets.Values
                .Where(bucket => bucket.Weight > 0)
                .Select(bucket => new PalettePoint(bucket.Red / bucket.Weight, bucket.Green / bucket.Weight,
                    bucket.Blue / bucket.Weight, bucket.Weight))
                .OrderByDescending(bucket => bucket.Weight)
                .ToList();
            if (seeds.Count == 0) return FallbackClusters(count);

            var centers = new List<PalettePoint>();
            while (centers.Count < count && centers.Count < seeds.Count)
            {
                PalettePoint choice = seeds
                    .Where(seed => !centers.Contains(seed))
                    .OrderByDescending(seed => centers.Count == 0
                        ? seed.Weight
                        : seed.Weight * Math.Sqrt(centers.Min(center => ColorDistanceSquared(seed, center))))
                    .First();
                centers.Add(new PalettePoint(choice.Red, choice.Green, choice.Blue, 1.0));
            }
            while (centers.Count < count)
                centers.Add(centers[centers.Count % Math.Max(1, centers.Count)]);

            for (int iteration = 0; iteration < 5; iteration++)
            {
                double[] redTotals = new double[count];
                double[] greenTotals = new double[count];
                double[] blueTotals = new double[count];
                double[] weights = new double[count];
                foreach (PalettePoint sample in samples)
                {
                    // Простой поиск минимума вместо OrderBy: без аллокаций и сортировки на каждую точку и итерацию.
                    // При равных расстояниях выбирается первый центр, как и у стабильной сортировки.
                    int nearest = 0;
                    double nearestDistance = ColorDistanceSquared(sample, centers[0]);
                    for (int candidate = 1; candidate < centers.Count; candidate++)
                    {
                        double distance = ColorDistanceSquared(sample, centers[candidate]);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearest = candidate;
                        }
                    }

                    redTotals[nearest] += sample.Red * sample.Weight;
                    greenTotals[nearest] += sample.Green * sample.Weight;
                    blueTotals[nearest] += sample.Blue * sample.Weight;
                    weights[nearest] += sample.Weight;
                }
                for (int index = 0; index < centers.Count; index++)
                {
                    if (weights[index] > 0)
                        centers[index] = new PalettePoint(redTotals[index] / weights[index], greenTotals[index] / weights[index],
                            blueTotals[index] / weights[index], weights[index]);
                }
            }

            return centers
                .OrderByDescending(center => center.Weight)
                .ToArray();
        }
        catch
        {
            return FallbackClusters(count);
        }
    }

    private static System.Windows.Media.Color ToColor(PalettePoint point) =>
        System.Windows.Media.Color.FromRgb((byte)Math.Clamp(point.Red, 0, 255),
            (byte)Math.Clamp(point.Green, 0, 255), (byte)Math.Clamp(point.Blue, 0, 255));

    private static double ColorDistanceSquared(PalettePoint first, PalettePoint second)
    {
        double red = first.Red - second.Red;
        double green = first.Green - second.Green;
        double blue = first.Blue - second.Blue;
        return red * red + green * green + blue * blue;
    }

    private static System.Windows.Media.Color[] CreateFallbackPalette() => new[]
    {
        System.Windows.Media.Color.FromRgb(82, 120, 255),
        System.Windows.Media.Color.FromRgb(82, 178, 235),
        System.Windows.Media.Color.FromRgb(158, 98, 224),
        System.Windows.Media.Color.FromRgb(240, 142, 104),
        System.Windows.Media.Color.FromRgb(92, 212, 166)
    };

    private static PalettePoint[] FallbackClusters(int count)
    {
        System.Windows.Media.Color[] palette = CreateFallbackPalette();
        return Enumerable.Range(0, count)
            .Select(index => new PalettePoint(palette[index % palette.Length].R, palette[index % palette.Length].G,
                palette[index % palette.Length].B, 1.0 / (1 + index)))
            .ToArray();
    }

    private static System.Windows.Media.Color MixColors(System.Windows.Media.Color first,
        System.Windows.Media.Color second, double secondWeight)
    {
        double weight = Math.Clamp(secondWeight, 0, 1);
        return System.Windows.Media.Color.FromRgb(
            (byte)(first.R * (1 - weight) + second.R * weight),
            (byte)(first.G * (1 - weight) + second.G * weight),
            (byte)(first.B * (1 - weight) + second.B * weight));
    }

    private void CancelLyricsLoad()
    {
        _lyricsLoadCts?.Cancel();
        _lyricsLoadCts?.Dispose();
        _lyricsLoadCts = null;
    }

    private void CancelOnlineSearch()
    {
        _onlineSearchCts?.Cancel();
        _onlineSearchCts?.Dispose();
        _onlineSearchCts = null;
    }

    private async Task LoadLyricsAsync(string? trackPath)
    {
        CancelLyricsLoad();
        _lyricsLoadCts = new CancellationTokenSource();
        CancellationToken token = _lyricsLoadCts.Token;

        _lyricsTrackPath = trackPath;
        SetLyrics(LyricsDocument.Empty);
        _activeLyricIndex = -2;
        ApplyLyricsDocument(_lyrics);

        try
        {
            LyricsDocument document = await LyricsService.LoadAsync(trackPath, token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _lyricsTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            SetLyrics(document);
            ApplyLyricsDocument(_lyrics);
            UpdateProgress(_owner.CurrentPlaybackSeconds, _owner.CurrentTrackDurationSeconds);

            // Если локального LRC/TXT и текста в теге нет, пробуем автоматически найти
            // только точное совпадение в LRCLIB. Неоднозначные варианты не подставляются
            // молча: их пользователь по-прежнему может выбрать через встроенную лупу.
            if (document.Kind == LyricsKind.None && !string.IsNullOrWhiteSpace(trackPath))
                await TryAutoFindLyricsAsync(trackPath, token);
        }
        catch (OperationCanceledException)
        {
            // Обычный переход между треками: более новая задача уже загрузит следующий текст.
        }
        catch (Exception ex)
        {
            // Любая сбойная загрузка показывает пустой текст; причина остаётся в журнале для разбора.
            Logger.Warn($"Не удалось загрузить текст песни для окна «Сейчас играет»: {ex.Message}");
            SetLyrics(LyricsDocument.Empty);
            ApplyLyricsDocument(_lyrics);
        }
    }

    // Подпись над текстом песни; loading показывает индикатор ожидания Material (поиск текста и вариантов).
    private void SetLyricsStatusText(string text, bool loading = false)
    {
        LyricsModeText.Text = text;
        LyricsLoadingIndicator.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetLyrics(LyricsDocument source)
    {
        _lyricsSource = source;
        _lyrics = LyricsService.ApplyDisplayMode(source, _owner.Settings.LyricsDisplayMode);
    }

    // Вызывается из MainWindow.RefreshLyricsDisplayMode после смены режима.
    internal void RefreshLyricsDisplayMode()
    {
        SetLyrics(_lyricsSource);
        ApplyLyricsDocument(_lyrics);
        UpdateProgress(_owner.CurrentPlaybackSeconds, _owner.CurrentTrackDurationSeconds);
    }

    private void LyricsDisplayModeButton_Click(object sender, RoutedEventArgs e) => _owner.ToggleLyricsDisplayMode();

    private async Task TryAutoFindLyricsAsync(string trackPath, CancellationToken lifetimeToken)
    {
        CancelOnlineSearch();
        var searchCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _onlineSearchCts = searchCts;
        CancellationToken token = searchCts.Token;

        if (_owner.Settings.LyricsSearchPolicy != "AutoExact")
        {
            SetLyricsStatusText(LocalizationService.Translate(_owner.Settings.LyricsSearchPolicy == "ManualOnly"
                ? "Текст можно найти вручную"
                : "Онлайн-поиск текста отключён"));
            return;
        }

        try
        {
            SetLyricsStatusText(LocalizationService.Translate("Ищем текст…"), loading: true);
            LyricsService.AutomaticLyricsLookup lookup = await LyricsService.FindAutomaticAsync(
                _owner.CurrentTitle, _owner.CurrentArtist, _owner.CurrentTrackDurationSeconds, token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            OnlineLyricsResult? exact = lookup.Best;
            if (exact is null)
            {
                SetLyricsStatusText(LocalizationService.Translate(lookup.CandidateCount > 0 ? "Нужен выбор варианта" : "Нет текста"));
                return;
            }

            await LyricsService.SaveOnlineResultAsync(trackPath, exact, token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            // Остальные уверенные совпадения запоминаем: кнопка «Другой вариант» покажет их без повторного поиска.
            ResetLyricsVariants(trackPath);
            _lyricsVariants.AddRange(lookup.Matches);
            _lyricsVariantIndex = Math.Max(0, _lyricsVariants.IndexOf(exact));
            _lyricsVariantsLrcLibLoaded = true;
            _lyricsVariantsExtraLoaded = lookup.Matches.Any(match => match.Source != "LRCLIB");

            SetLyrics(LyricsService.CreateDocumentFromOnlineResult(exact));
            ApplyLyricsDocument(_lyrics);
            UpdateProgress(_owner.CurrentPlaybackSeconds, _owner.CurrentTrackDurationSeconds);
        }
        catch (LyricsRateLimitException)
        {
            SetLyricsStatusText(LocalizationService.Translate("Поиск временно ограничен"));
        }
        catch (OperationCanceledException)
        {
            // Нормально при смене трека или запуске ручного поиска.
        }
        catch
        {
            SetLyricsStatusText(LocalizationService.Translate("Нет текста"));
        }
        finally
        {
            if (ReferenceEquals(_onlineSearchCts, searchCts))
                _onlineSearchCts = null;
            searchCts.Dispose();
        }
    }

    private void ResetLyricsVariants(string? trackPath)
    {
        _lyricsVariants.Clear();
        _lyricsVariantsTrackPath = trackPath;
        _lyricsVariantIndex = -1;
        _lyricsVariantsLrcLibLoaded = false;
        _lyricsVariantsExtraLoaded = false;
    }

    private void NextLyricsVariantButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ShowNextLyricsVariantAsync(), nameof(NextLyricsVariantButton_Click));

    // «Другой вариант»: переключает текущий трек на следующий найденный текст (с кругом по списку) и сохраняет его так же,
    // как выбор в ручном поиске. Если известные варианты закончились, один раз догружает дополнительные источники.
    private async Task ShowNextLyricsVariantAsync()
    {
        if (_lyricsVariantBusy) return;

        string? trackPath = _owner.CurrentTrackPath;
        if (string.IsNullOrWhiteSpace(trackPath) || !File.Exists(trackPath)) return;

        if (_owner.Settings.LyricsSearchPolicy == "LocalOnly")
        {
            SetLyricsStatusText(LocalizationService.Translate("Онлайн-поиск текста отключён"));
            return;
        }

        if (!string.Equals(trackPath, _lyricsVariantsTrackPath, StringComparison.OrdinalIgnoreCase))
            ResetLyricsVariants(trackPath);

        CancelOnlineSearch();
        var searchCts = new CancellationTokenSource();
        _onlineSearchCts = searchCts;
        CancellationToken token = searchCts.Token;

        _lyricsVariantBusy = true;
        NextLyricsVariantButton.IsEnabled = false;
        try
        {
            string title = _owner.CurrentTitle, artist = _owner.CurrentArtist;
            double duration = _owner.CurrentTrackDurationSeconds;

            void AddVariants(IReadOnlyList<OnlineLyricsResult> found)
            {
                foreach (OnlineLyricsResult result in found)
                {
                    if (!_lyricsVariants.Any(existing => existing.Source == result.Source && existing.Id == result.Id))
                        _lyricsVariants.Add(result);
                }
            }

            if (!_lyricsVariantsLrcLibLoaded)
            {
                SetLyricsStatusText(LocalizationService.Translate("Ищем другие варианты…"), loading: true);
                AddVariants(await LyricsService.FindLrcLibVariantsAsync(title, artist, duration, token));
                _lyricsVariantsLrcLibLoaded = true;
            }

            if (_lyricsVariantIndex + 1 >= _lyricsVariants.Count && !_lyricsVariantsExtraLoaded)
            {
                SetLyricsStatusText(LocalizationService.Translate("Ищем другие варианты…"), loading: true);
                AddVariants(await LyricsService.FindExtraVariantsAsync(title, artist, duration, token));
                _lyricsVariantsExtraLoaded = true;
            }

            if (token.IsCancellationRequested || !string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (_lyricsVariants.Count == 0 || (_lyricsVariants.Count == 1 && _lyricsVariantIndex == 0))
            {
                SetLyricsStatusText(LocalizationService.Translate(_lyricsVariants.Count == 0
                    ? "Подходящих вариантов не найдено"
                    : "Других вариантов нет"));
                return;
            }

            int next = (_lyricsVariantIndex + 1) % _lyricsVariants.Count;
            await LyricsService.SaveOnlineResultAsync(trackPath, _lyricsVariants[next], token);
            if (token.IsCancellationRequested || !string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            _lyricsVariantIndex = next;
            // Перечитываем текст обычным путём, как после выбора в ручном поиске, чтобы показать ровно то, что сохранено.
            _lyricsTrackPath = null;
            await LoadLyricsAsync(trackPath);
            LyricsModeText.Text += $" · {next + 1}/{_lyricsVariants.Count}";
        }
        catch (LyricsRateLimitException)
        {
            SetLyricsStatusText(LocalizationService.Translate("Поиск временно ограничен"));
        }
        catch (OperationCanceledException)
        {
            // Смена трека или запуск другого поиска.
        }
        catch
        {
            SetLyricsStatusText(LocalizationService.Translate("Не удалось сменить вариант текста"));
        }
        finally
        {
            if (ReferenceEquals(_onlineSearchCts, searchCts))
                _onlineSearchCts = null;
            searchCts.Dispose();
            _lyricsVariantBusy = false;
            NextLyricsVariantButton.IsEnabled = true;
        }
    }

    private void ApplyLyricsDocument(LyricsDocument document)
    {
        _syncedLines.Clear();
        foreach (LyricLine line in document.Lines)
            _syncedLines.Add(line);

        SetLyricsStatusText(LocalizationService.Translate(document.SourceLabel));
        SyncedLyricsList.Visibility = document.Kind == LyricsKind.Synced ? Visibility.Visible : Visibility.Collapsed;
        PlainLyricsScroll.Visibility = document.Kind == LyricsKind.Plain ? Visibility.Visible : Visibility.Collapsed;
        NoLyricsPanel.Visibility = document.Kind == LyricsKind.None ? Visibility.Visible : Visibility.Collapsed;
        PlainLyricsText.Text = document.Kind == LyricsKind.Plain ? document.PlainText : string.Empty;
        _activeLyricIndex = -2;
        ResetLyricsScroll();
        // Контейнеры строк появляются после layout: до этого выставлять им состояние нечему.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            ApplyLyricLineStates(0, _syncedLines.Count - 1, animate: false)));

        bool plainMode = _owner.Settings.LyricsDisplayMode == "Plain";
        LyricsDisplayModeButton.Visibility = _lyricsSource.Kind == LyricsKind.Synced ? Visibility.Visible : Visibility.Collapsed;
        LyricsDisplayModeButton.Content = plainMode ? "TXT" : "LRC";
        LyricsDisplayModeButton.ToolTip = LocalizationService.Translate(plainMode ? "Показать синхронный текст" : "Показать обычный текст");
    }

    // У активной строки радиус не нулевой: при нулевом WPF обходит эффект, и строка меняла способ отрисовки в конце анимации.
    private const double ActiveLineBlurRadius = 0.01;
    private const double InactiveLineOpacity = 0.65;
    private const double MinimumInactiveOpacity = 0.15;

    // Подложка, полоска, масштаб и затухание строк зависят от настроек выделения и от расстояния до активной строки,
    // поэтому задаются кодом, а не триггерами шаблона. Строки вне диапазона не трогаем: их цель не изменилась.
    private void ApplyLyricLineStates(int fromIndex, int toIndex, bool animate)
    {
        AppSettings settings = _owner.Settings;
        bool pill = LyricsHighlightStyle.HasPill(settings.SyncedLyricsHighlightEffect);
        bool bar = LyricsHighlightStyle.HasBar(settings.SyncedLyricsHighlightEffect);
        bool scale = settings.SyncedLyricsScaleActiveLine;
        bool fade = settings.SyncedLyricsFadeDistantLines;
        bool blur = settings.SyncedLyricsBlurInactive;
        double fadeStep = LyricsHighlightStyle.FadeStep(settings.SyncedLyricsFadeStrength);
        double activeScale = LyricsHighlightStyle.ScaleFactor(settings.SyncedLyricsScalePercent);
        int visibleRadius = LyricsHighlightStyle.VisibleRadius(LyricsHighlightStyle.NormalizeVisibleLines(settings.SyncedLyricsVisibleLines));
        bool animateNow = animate && !AccessibilityPreferences.ShouldReduceMotion(settings, AnimationAreas.Lyrics);
        double originX = LyricsTextAlignmentMode.ToTextAlignment(settings.LyricsTextAlignment) switch
        {
            TextAlignment.Center => 0.5,
            TextAlignment.Right => 1.0,
            _ => 0.0,
        };
        // Увеличенный текст не должен упираться в край списка, поэтому при включённом масштабе у строк шире поля.
        double sidePadding = scale ? LyricsHighlightStyle.ScaleSidePadding(settings.SyncedLyricsScalePercent) : 14;
        var padding = new Thickness(sidePadding, 10, sidePadding, 10);
        int anchor = Math.Max(_activeLyricIndex, -1);

        for (int i = Math.Max(0, fromIndex); i <= Math.Min(_syncedLines.Count - 1, toIndex); i++)
        {
            if (SyncedLyricsList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;

            bool active = i == _activeLyricIndex;
            int distance = Math.Abs(i - anchor);
            double opacity = active
                ? 1.0
                : fade ? Math.Clamp(InactiveLineOpacity - fadeStep * (distance - 1), MinimumInactiveOpacity, InactiveLineOpacity) : InactiveLineOpacity;

            // За пределами окна видимых строк текст скрыт, а крайние строки окна — вполовину бледнее.
            bool inWindow = active || distance <= visibleRadius;
            if (!inWindow) opacity = 0;
            else if (!active && visibleRadius > 0 && distance == visibleRadius) opacity *= 0.55;
            item.IsHitTestVisible = inWindow;

            item.Padding = padding;
            AnimateTextAlpha(item, opacity, animateNow);

            item.ApplyTemplate();
            if (item.Template?.FindName("Bar", item) is Border barElement)
                AnimateOpacity(barElement, active && bar ? 1.0 : 0.0, animateNow);

            if (item.Template?.FindName("Content", item) is ContentPresenter content)
            {
                // Размытие только у неактивных строк рядом с активной; дальние строки и выключенная настройка — без эффекта.
                bool blurHere = blur && inWindow && distance <= LyricsHighlightStyle.BlurReachLines;
                ApplyBlur(content, blurHere, active ? ActiveLineBlurRadius : LyricsHighlightStyle.BlurRadius(settings.SyncedLyricsBlurStrength, distance), animateNow);

                content.RenderTransformOrigin = new Point(originX, 0.5);
                // Трансформ из шаблона заморожен и не анимируется, поэтому у каждой строки заводим собственный.
                if (content.RenderTransform is not ScaleTransform { IsFrozen: false } transform)
                {
                    transform = new ScaleTransform(1, 1);
                    content.RenderTransform = transform;
                }
                double target = active && scale ? activeScale : 1.0;
                AnimateScale(transform, target, animateNow);
            }

            AnimatePillColor(item, active && pill ? Color.FromArgb(0x3B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), animateNow);
        }
    }

    // Строки, чьё состояние могло измениться при смене активной: две строки или, при затухании, окрестность обеих.
    private void ApplyLyricLineStatesForChange(int previous, int current)
    {
        AppSettings settings = _owner.Settings;
        int visible = LyricsHighlightStyle.NormalizeVisibleLines(settings.SyncedLyricsVisibleLines);
        if (settings.SyncedLyricsFadeDistantLines || settings.SyncedLyricsBlurInactive || visible > 0)
        {
            // Затронуты строки, у которых цель зависит от расстояния до активной: окрестность старой и новой позиции.
            int fadeReach = settings.SyncedLyricsFadeDistantLines
                ? (int)Math.Ceiling((InactiveLineOpacity - MinimumInactiveOpacity) / LyricsHighlightStyle.FadeStep(settings.SyncedLyricsFadeStrength)) + 1
                : 0;
            int blurReach = settings.SyncedLyricsBlurInactive ? Math.Min(LyricsHighlightStyle.BlurReachLines, visible > 0 ? LyricsHighlightStyle.VisibleRadius(visible) : LyricsHighlightStyle.BlurReachLines) : 0;
            int reach = Math.Max(Math.Max(fadeReach, blurReach), visible > 0 ? LyricsHighlightStyle.VisibleRadius(visible) : 0) + 1;
            int low = Math.Min(Math.Max(previous, 0), Math.Max(current, 0)) - reach;
            int high = Math.Max(previous, current) + reach;
            ApplyLyricLineStates(low, high, animate: true);
            return;
        }

        if (previous >= 0) ApplyLyricLineStates(previous, previous, animate: true);
        if (current >= 0) ApplyLyricLineStates(current, current, animate: true);
    }

    private static readonly Duration LineStateDuration = new(TimeSpan.FromMilliseconds(320));

    // Яркость текста строки задаётся альфа-каналом цвета (Foreground наследуется текстом), а не Opacity элемента:
    // так строка и её свечение всегда рисуются одним и тем же путём, без скачка в конце анимации.
    private static void AnimateTextAlpha(ListBoxItem item, double alpha, bool animate)
    {
        Color to = Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 0xFF, 0xFF, 0xFF);
        if (item.Foreground is not SolidColorBrush { IsFrozen: false } brush)
        {
            // Стартуем с цвета из стиля (он заморожен), иначе строка на миг вспыхнула бы белым.
            brush = new SolidColorBrush(item.Foreground is SolidColorBrush style ? style.Color : to);
            item.Foreground = brush;
        }

        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }

        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(to, LineStateDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateOpacity(UIElement element, double to, bool animate)
    {
        if (!animate)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, LineStateDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } },
            HandoffBehavior.SnapshotAndReplace);
    }

    // BlurEffect остаётся на строке даже при нулевом радиусе (у активной): так отрисовка текста не переключается скачком.
    private static void ApplyBlur(UIElement element, bool enabled, double radius, bool animate)
    {
        if (!enabled)
        {
            if (element.Effect is System.Windows.Media.Effects.BlurEffect existing)
            {
                existing.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                element.Effect = null;
            }
            return;
        }

        if (element.Effect is not System.Windows.Media.Effects.BlurEffect { IsFrozen: false } blur)
        {
            blur = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 0,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            };
            element.Effect = blur;
        }

        if (!animate)
        {
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
            blur.Radius = radius;
            return;
        }

        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty,
            new DoubleAnimation(radius, LineStateDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateScale(ScaleTransform transform, double to, bool animate)
    {
        if (!animate)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = to;
            transform.ScaleY = to;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, LineStateDuration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, LineStateDuration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimatePillColor(ListBoxItem item, Color to, bool animate)
    {
        if (item.Background is not SolidColorBrush { IsFrozen: false } brush)
        {
            brush = new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
            item.Background = brush;
        }

        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }

        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(to, LineStateDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } },
            HandoffBehavior.SnapshotAndReplace);
    }

    // Новый текст всегда показывается с начала: ScrollViewer сохраняет смещение, если Clear + Add происходят до layout,
    // а незавершённая анимация прокрутки держит прежнюю позицию.
    private void ResetLyricsScroll()
    {
        if (FindScrollViewer(SyncedLyricsList) is { } viewer)
        {
            viewer.BeginAnimation(AnimatedScrollOffsetProperty, null);
            viewer.ScrollToTop();
        }
        PlainLyricsScroll.ScrollToTop();
    }

    private void UpdatePlaybackState(bool isPlaying)
    {
        ArtworkProgressMaterial.IsWaving = isPlaying;
        UpdateAmbientAnimation(isPlaying);
        string icon = isPlaying ? "IconPause" : "IconPlay";
        string toolTip = LocalizationService.Translate(isPlaying ? "Пауза" : "Воспроизвести");
        ArtworkPlayPauseIcon.Icon = icon;
        ArtworkPlayPauseButton.ToolTip = toolTip;
    }

    private void InitializeAmbientAnimation()
    {
        if (_ambientMotionTimer is not null) return;

        _ambientMotionTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _ambientMotionTimer.Tick += AmbientMotionTimer_Tick;

        // Возобновляем и останавливаем фоновые анимации при показе, сворачивании и скрытии окна.
        IsVisibleChanged += (_, _) => UpdateAmbientAnimation(_owner.IsPlayingNow);
        StateChanged += (_, _) => UpdateAmbientAnimation(_owner.IsPlayingNow);
        UpdateAmbientAnimation(_owner.IsPlayingNow);
    }

    private bool IsShownOnScreen => IsVisible && WindowState != WindowState.Minimized;

    private void AmbientMotionTimer_Tick(object? sender, EventArgs e)
    {
        // Скорость облаков берётся из реальной энергии уже обработанного аудиосигнала. Корень
        // делает реакцию заметной и в тихих фрагментах, а сглаживание _ambientSpeed ниже
        // сохраняет расслабляющее движение без резких рывков на каждом ударе.
        if (AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.NowPlaying))
        {
            // Сцена остаётся визуально цельной, но прекращает декоративное движение сразу.
            _ambientSpeed = 0;
            _ambientTargetSpeed = 0;
            _ambientMotionTimer?.Stop();
            return;
        }

        if (_owner.IsPlayingNow)
        {
            double level = Math.Clamp(_owner.AudioLevelMeter?.NormalizedLevel ?? 0d, 0d, 1d);
            double shapedLevel = Math.Sqrt(level);
            _ambientTargetSpeed = 0.72 + shapedLevel * 0.88;
        }
        else
        {
            _ambientTargetSpeed = 0;
        }

        // Шаг считается по реальному времени между тиками: DispatcherTimer запаздывает, когда UI занят (прокрутка текста,
        // смена трека), и фиксированный шаг на тик давал рывки. 30 кадров/с остаются эталоном прежней скорости.
        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = _ambientLastTimestamp == 0
            ? 1.0 / 30.0
            : Math.Clamp((now - _ambientLastTimestamp) / (double)Stopwatch.Frequency, 0.0, 0.1);
        _ambientLastTimestamp = now;
        double frameFactor = elapsedSeconds * 30.0;

        // Скорость перелива сглаживается, поэтому удары в музыке ускоряют фон плавно, без рывков.
        _ambientSpeed += (_ambientTargetSpeed - _ambientSpeed) * (1.0 - Math.Pow(1.0 - 0.032, frameFactor));
        _ambientMotionTime += 0.015 * frameFactor * _ambientSpeed;
        RenderCloudFlowFrame(elapsedSeconds);

        _ambientScenePlaced = true;
        if (!_owner.IsPlayingNow && _ambientSpeed <= AmbientIdleSpeedThreshold)
            _ambientMotionTimer?.Stop();
    }

    private void UpdateAmbientAnimation(bool isPlaying)
    {
        // При воспроизведении значение уточняется по AudioLevelMeter на каждом тике;
        // здесь задаётся спокойная стартовая скорость до первого аудиосэмпла.
        bool reduceMotion = AccessibilityPreferences.ShouldReduceMotion(_owner.Settings, AnimationAreas.NowPlaying);
        _ambientTargetSpeed = reduceMotion ? 0 : isPlaying ? 0.72 : 0;
        if (reduceMotion) _ambientSpeed = 0;
        // Таймер нужен только облакам; шары и волны считаются покадрово. На паузе облака плавно тормозят до нуля
        // и таймер выключается; при «Меньше анимации» он не работает вовсе.
        if (_ambientMotionTimer is not null)
        {
            // В свёрнутом или скрытом окне анимацию никто не видит: покадровый расчёт не нужен.
            _ambientMotionTimer.IsEnabled = BackgroundMode == "Clouds" && !reduceMotion && IsShownOnScreen
                && (isPlaying || _ambientSpeed > AmbientIdleSpeedThreshold || !_ambientScenePlaced);
        }
        // Измеритель уровня нужен только реагирующим фонам и только пока окно видно.
        bool animatedBackground = BackgroundMode == "Clouds" || IsOrbsBackground || IsWavesBackground;
        _owner.SetAudioLevelMeterEnabled(animatedBackground && !reduceMotion && IsShownOnScreen);
        UpdateDynamicDriver(isPlaying);
    }

    private void UpdateProgress(double currentSeconds, double totalSeconds)
    {
        double current = Math.Max(currentSeconds, 0);
        double total = Math.Max(totalSeconds, 0);
        PlaybackTimeText.Text = TimeSpan.FromSeconds(current).ToString(@"mm\:ss");
        PlaybackDurationText.Text = TimeSpan.FromSeconds(total).ToString(@"mm\:ss");
        ArtworkProgressBar.Maximum = total > 0 ? total : 1;
        ArtworkProgressBar.Value = Math.Min(current, ArtworkProgressBar.Maximum);
        double ratio = total > 0 ? Math.Clamp(current / total, 0.0, 1.0) : 0.0;
        ArtworkProgressMaterial.Progress = ratio;
        ArtworkProgressMaterialSlider.Progress = ratio;

        if (_lyrics.Kind != LyricsKind.Synced) return;
        int index = LyricsService.FindActiveLineIndex(_lyrics.Lines, TimeSpan.FromSeconds(current));
        if (index == _activeLyricIndex) return;

        int previousIndex = _activeLyricIndex;
        _activeLyricIndex = index;
        SyncedLyricsList.SelectedIndex = index;
        ApplyLyricLineStatesForChange(previousIndex, index);
        if (index >= 0 && index < _syncedLines.Count)
        {
            // Контейнер строки может появиться только после layout, поэтому и центрирование, и свечение — после него.
            LyricLine activeLine = _syncedLines[index];
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_activeLyricIndex != index) return;
                RefreshActiveLyricGlow();
                SmoothScrollLyricsToLine(activeLine);
            }));
        }
        else
        {
            RefreshActiveLyricGlow();
        }
    }

    private void ArtworkProgressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ArtworkProgressHost.ActualWidth <= 0 || _owner.CurrentTrackDurationSeconds <= 0) return;

        double clickX = e.GetPosition(ArtworkProgressHost).X;
        double ratio = Math.Clamp(clickX / ArtworkProgressHost.ActualWidth, 0.0, 1.0);
        _owner.ExternalSeekRatio(ratio);
        e.Handled = true;
    }

    private void ArtworkSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        if (ArtworkOverlayAllowed) AnimateArtworkControls(visible: true);
    }

    private void ArtworkSurface_MouseLeave(object sender, MouseEventArgs e) => AnimateArtworkControls(visible: false);

    private void AnimateArtworkControls(bool visible)
    {
        var animation = new DoubleAnimation(visible ? 1.0 : 0.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ArtworkControlsOverlay.BeginAnimation(OpacityProperty, animation);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPlayPause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalNext();
    private void PreviousButton_Click(object sender, RoutedEventArgs e) => _owner.ExternalPrev();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void FindLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTrackPath(out _)) return;

        CancelOnlineSearch();
        _onlineResults.Clear();
        OnlineLyricsSearchPanel.Visibility = Visibility.Visible;
        LyricsSearchQueryTextBox.Text = BuildDefaultLyricsSearchQuery();
        OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Проверьте запрос и нажмите «Искать» или Enter.");
        LyricsSearchQueryTextBox.Focus();
        LyricsSearchQueryTextBox.SelectAll();
    }

    private void RunLyricsSearchButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(RunLyricsSearchButton_ClickAsync(sender, e), nameof(RunLyricsSearchButton_Click));

    private async Task RunLyricsSearchButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTrackPath(out string trackPath)) return;
        await SearchLyricsAsync(trackPath);
    }

    private void LyricsSearchQueryTextBox_KeyDown(object sender, KeyEventArgs e)
        => BackgroundTask.FireAndForget(LyricsSearchQueryTextBox_KeyDownAsync(sender, e), nameof(LyricsSearchQueryTextBox_KeyDown));

    private async Task LyricsSearchQueryTextBox_KeyDownAsync(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!TryGetCurrentTrackPath(out string trackPath)) return;
        await SearchLyricsAsync(trackPath);
    }

    private bool TryGetCurrentTrackPath(out string trackPath)
    {
        trackPath = _owner.CurrentTrackPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trackPath) && File.Exists(trackPath)) return true;

        LocalizedMessageBox.Show(this, "Сначала выберите или запустите трек.", "Поиск текста",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private string BuildDefaultLyricsSearchQuery()
    {
        string title = _owner.CurrentTitle.Trim();
        string artist = _owner.CurrentArtist.Trim();
        return string.IsNullOrWhiteSpace(artist) || artist == "—"
            ? title
            : $"{artist} — {title}";
    }

    private static (string TrackName, string ArtistName) ParseLyricsSearchQuery(string query)
    {
        string text = query.Trim();
        int separator = text.IndexOf(" — ", StringComparison.Ordinal);
        int separatorLength = 3;
        if (separator < 0)
        {
            separator = text.IndexOf(" - ", StringComparison.Ordinal);
            separatorLength = 3;
        }

        return separator > 0 && separator + separatorLength < text.Length
            ? (text[(separator + separatorLength)..].Trim(), text[..separator].Trim())
            : (text, string.Empty);
    }

    private async Task SearchLyricsAsync(string trackPath)
    {
        (string trackName, string artistName) = ParseLyricsSearchQuery(LyricsSearchQueryTextBox.Text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(trackName))
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Введите название трека или «исполнитель — название».");
            LyricsSearchQueryTextBox.Focus();
            return;
        }

        CancelOnlineSearch();
        var searchCts = new CancellationTokenSource();
        _onlineSearchCts = searchCts;
        CancellationToken token = searchCts.Token;
        _onlineResults.Clear();
        OnlineLyricsSearchPanel.Visibility = Visibility.Visible;
        OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Ищу текст…");
        FindLyricsButton.IsEnabled = false;
        RunLyricsSearchButton.IsEnabled = false;
        if (_owner.Settings.LyricsSearchPolicy == "LocalOnly")
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Онлайн-поиск текста отключён");
            FindLyricsButton.IsEnabled = true;
            RunLyricsSearchButton.IsEnabled = true;
            LyricsSearchQueryTextBox.IsEnabled = true;
            return;
        }

        LyricsSearchQueryTextBox.IsEnabled = false;

        try
        {
            IReadOnlyList<OnlineLyricsResult> results = await LyricsService.SearchOnlineVariantsAsync(trackName, artistName, token);
            if (token.IsCancellationRequested)
                return; // отмена: статус принадлежит новому поиску или закрытию окна

            if (!string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
            {
                // Трек сменился, пока шёл запрос: результаты относятся к другому треку, а статус «Ищу текст…» не должен залипнуть.
                HideOnlineLyricsSearch();
                return;
            }

            foreach (OnlineLyricsResult result in results)
                _onlineResults.Add(result);

            OnlineLyricsSearchStatusText.Text = _onlineResults.Count == 0
                ? LocalizationService.Translate("Во встроенной базе совпадения не найдены. Измените запрос или откройте Genius ниже.")
                : LocalizationService.Format("Найдено вариантов: {0}. Дважды кликните нужный вариант, чтобы сохранить его.", _onlineResults.Count);
        }
        catch (LyricsRateLimitException ex)
        {
            string wait = ex.RetryAfter is { } delay && delay > TimeSpan.Zero
                ? LocalizationService.Format(" Попробуйте снова через {0} сек.", Math.Ceiling(delay.TotalSeconds))
                : LocalizationService.Translate(" Попробуйте снова немного позже.");
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Сервис временно ограничил запросы.") + wait;
        }
        catch (OperationCanceledException)
        {
            // Поиск отменён при смене трека, новом запросе или закрытии окна.
        }
        catch (Exception ex)
        {
            Logger.Error("Онлайн-поиск текста песни не удался", ex);
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Не удалось выполнить поиск. Проверьте подключение к интернету.");
        }
        finally
        {
            if (ReferenceEquals(_onlineSearchCts, searchCts))
                _onlineSearchCts = null;
            searchCts.Dispose();
            FindLyricsButton.IsEnabled = true;
            RunLyricsSearchButton.IsEnabled = true;
            LyricsSearchQueryTextBox.IsEnabled = true;
        }
    }

    private void OpenGeniusSearchButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLyricsSearch("https://genius.com/search?q=");

    private void PasteLyricsFromClipboardButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(PasteLyricsFromClipboardButton_ClickAsync(sender, e), nameof(PasteLyricsFromClipboardButton_Click));

    private async Task PasteLyricsFromClipboardButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentTrackPath(out string trackPath)) return;

        string text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Сначала скопируйте текст песни в буфер обмена.");
                return;
            }

            text = Clipboard.GetText().Trim();
        }
        catch
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Не удалось прочитать буфер обмена. Скопируйте текст ещё раз.");
            return;
        }

        const int maxLyricsBytes = 2 * 1024 * 1024;
        if (string.IsNullOrWhiteSpace(text))
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("В буфере нет текста песни.");
            return;
        }
        if (Encoding.UTF8.GetByteCount(text) > maxLyricsBytes)
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Скопированный текст больше 2 МБ и не был сохранён.");
            return;
        }

        try
        {
            // Вставленный текст хранится в %LocalAppData%\\Lumisense\\lyrics\\txt, а не рядом с музыкой.
            await LyricsService.SavePastedLyricsAsync(trackPath, text);
            if (!string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            HideOnlineLyricsSearch();
            _lyricsTrackPath = null;
            await LoadLyricsAsync(trackPath);
        }
        catch (Exception ex)
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate($"Не удалось сохранить текст: {ex.Message}");
        }
    }

    private string GetLyricsSearchQuery() =>
        (LyricsSearchQueryTextBox.Text ?? BuildDefaultLyricsSearchQuery()).Trim();

    private void OpenExternalLyricsSearch(string baseUrl)
    {
        string query = GetLyricsSearchQuery();
        if (string.IsNullOrWhiteSpace(query))
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Введите запрос, чтобы открыть внешний поиск.");
            LyricsSearchQueryTextBox.Focus();
            return;
        }

        try
        {
            string url = baseUrl + Uri.EscapeDataString(query);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Не удалось открыть браузер для внешнего поиска.");
        }
    }

    private void OnlineLyricsResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => BackgroundTask.FireAndForget(OnlineLyricsResultsList_MouseDoubleClickAsync(sender, e), nameof(OnlineLyricsResultsList_MouseDoubleClick));

    private async Task OnlineLyricsResultsList_MouseDoubleClickAsync(object sender, MouseButtonEventArgs e)
    {
        if (OnlineLyricsResultsList.SelectedItem is not OnlineLyricsResult result) return;
        string? trackPath = _owner.CurrentTrackPath;
        if (string.IsNullOrWhiteSpace(trackPath) || !File.Exists(trackPath)) return;

        OnlineLyricsSearchStatusText.Text = LocalizationService.Translate("Сохраняю выбранный текст…");
        OnlineLyricsResultsList.IsEnabled = false;
        try
        {
            await LyricsService.SaveOnlineResultAsync(trackPath, result, CancellationToken.None);
            if (!string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            HideOnlineLyricsSearch();
            _lyricsTrackPath = null;
            await LoadLyricsAsync(trackPath);
        }
        catch (Exception ex)
        {
            OnlineLyricsSearchStatusText.Text = LocalizationService.Translate($"Не удалось сохранить текст: {ex.Message}");
            OnlineLyricsResultsList.IsEnabled = true;
        }
    }

    private void CloseOnlineLyricsSearchButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOnlineSearch();
        HideOnlineLyricsSearch();
    }

    private void HideOnlineLyricsSearch()
    {
        OnlineLyricsSearchPanel.Visibility = Visibility.Collapsed;
        OnlineLyricsResultsList.IsEnabled = true;
        FindLyricsButton.IsEnabled = true;
        RunLyricsSearchButton.IsEnabled = true;
        LyricsSearchQueryTextBox.IsEnabled = true;
    }

    private void ImportLyricsButton_Click(object sender, RoutedEventArgs e)
        => BackgroundTask.FireAndForget(ImportLyricsButton_ClickAsync(sender, e), nameof(ImportLyricsButton_Click));

    private async Task ImportLyricsButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        string? trackPath = _owner.CurrentTrackPath;
        if (string.IsNullOrWhiteSpace(trackPath) || !File.Exists(trackPath))
        {
            LocalizedMessageBox.Show(this, "Сначала выберите или запустите трек.", "Загрузка текста",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Translate("Выберите текст песни"),
            Filter = LocalizationService.Translate("Синхронный текст LRC (*.lrc)|*.lrc|Обычный текст (*.txt)|*.txt"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var source = new FileInfo(dialog.FileName);
            const long maxLyricsFileSize = 2 * 1024 * 1024;
            if (source.Length > maxLyricsFileSize)
            {
                LocalizedMessageBox.Show(this, "Файл текста больше 2 МБ и не был добавлен.", "Загрузка текста",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string extension = Path.GetExtension(source.Name).Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                ? ".lrc"
                : ".txt";
            string destination = Path.ChangeExtension(trackPath, extension);
            if (!string.Equals(source.FullName, destination, StringComparison.OrdinalIgnoreCase))
            {
                // Рядом может лежать собственный .lrc/.txt пользователя (ручной или от другого плеера): без вопроса и резервной
                // копии он бы молча пропал.
                if (File.Exists(destination))
                {
                    string existingName = Path.GetFileName(destination);
                    var replace = LocalizedMessageBox.Show(this,
                        $"Рядом с треком уже есть файл «{existingName}». Заменить его выбранным текстом?",
                        "Загрузка текста", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (replace != MessageBoxResult.Yes) return;
                }

                File.Copy(source.FullName, destination, overwrite: true);
            }

            // Нельзя применять текст, если за время выбора файла успел смениться трек.
            if (!string.Equals(trackPath, _owner.CurrentTrackPath, StringComparison.OrdinalIgnoreCase))
                return;

            HideOnlineLyricsSearch();
            _lyricsTrackPath = null;
            await LoadLyricsAsync(trackPath);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось добавить текст песни.\n\n{ex.Message}", "Загрузка текста",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NowPlayingWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            if (OnlineLyricsSearchPanel.Visibility == Visibility.Visible && e.Key == Key.Escape)
                CloseOnlineLyricsSearchButton_Click(this, e);
            else
                Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            _owner.ExternalPlayPause();
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            _owner.ExternalNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            _owner.ExternalPrev();
            e.Handled = true;
        }
    }

    private void SyncedLyricsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_lyrics.Kind != LyricsKind.Synced || _owner.CurrentTrackDurationSeconds <= 0)
            return;

        if (ItemsControl.ContainerFromElement(SyncedLyricsList, e.OriginalSource as DependencyObject)
            is not ListBoxItem { DataContext: LyricLine line })
            return;

        double ratio = Math.Clamp(line.Time.TotalSeconds / _owner.CurrentTrackDurationSeconds, 0.0, 1.0);
        _owner.ExternalSeekRatio(ratio);
        e.Handled = true;
    }

    // Выделение ставит только воспроизведение: иначе нажатие мыши выделяло бы строку сразу, а после перемотки подсветка прыгала бы обратно.
    private void SyncedLyricsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_lyrics.Kind == LyricsKind.Synced) e.Handled = true;
    }

    // Программное изменение SelectionChanged используется только для подсветки и автопрокрутки.
    private void SyncedLyricsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
    }
}
