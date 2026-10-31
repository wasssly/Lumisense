using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Карточка «Полоса воспроизведения»: вид полосы (стиль у каждого окна свой или общий) и её внешний вид (ProgressBarAppearance).
// Правит профиль общий или одного из окон; предпросмотр рисуют настоящие контролы выбранного вида и закрепляется вверху карточки.
public partial class SettingsWindow
{
    private ProgressSurface _progressEditSurface = ProgressSurface.Main;

    // Пока true, изменения контролов не пишутся в настройки: так XAML-значения ползунков при загрузке окна ничего не портят.
    private bool _progressEditorLoading = true;

    private readonly DispatcherTimer _progressSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private ProgressBarAppearance EditedProgressAppearance =>
        _settings.ProgressAppearanceScope == "PerWindow"
            ? ProgressAppearanceApplier.For(_settings, _progressEditSurface)
            : _settings.ProgressAppearanceMain;

    private ProgressSurface EditedProgressSurface =>
        _settings.ProgressAppearanceScope == "PerWindow" ? _progressEditSurface : ProgressSurface.Main;

    private IEnumerable<(Slider Slider, TextBlock Text, Func<ProgressBarAppearance, double> Get, Action<ProgressBarAppearance, double> Set)> ProgressSliders()
    {
        yield return (ProgressThicknessSlider, ProgressThicknessValueText, a => a.TrackThickness, (a, v) => a.TrackThickness = v);
        yield return (ProgressThumbSlider, ProgressThumbValueText, a => a.ThumbSize, (a, v) => a.ThumbSize = v);
        yield return (ProgressWaveAmplitudeSlider, ProgressWaveAmplitudeValueText, a => a.WaveAmplitude, (a, v) => a.WaveAmplitude = v);
        yield return (ProgressWaveLengthSlider, ProgressWaveLengthValueText, a => a.WaveLength, (a, v) => a.WaveLength = v);
        yield return (ProgressWaveSpeedSlider, ProgressWaveSpeedValueText, a => a.WaveSpeed, (a, v) => a.WaveSpeed = v);
        yield return (ProgressWaveformGapSlider, ProgressWaveformGapValueText, a => a.WaveformGap, (a, v) => a.WaveformGap = v);
        yield return (ProgressWaveformOpacitySlider, ProgressWaveformOpacityValueText, a => a.WaveformUnplayedOpacity, (a, v) => a.WaveformUnplayedOpacity = v);
    }

    private void InitializeProgressAppearanceEditor()
    {
        _progressEditorLoading = true;
        try
        {
            bool perWindow = _settings.ProgressAppearanceScope == "PerWindow";
            ProgressScopePerWindowRadio.IsChecked = perWindow;
            ProgressScopeSharedRadio.IsChecked = !perWindow;
            ProgressSurfaceMainRadio.IsChecked = _progressEditSurface == ProgressSurface.Main;
            ProgressSurfaceNowPlayingRadio.IsChecked = _progressEditSurface == ProgressSurface.NowPlaying;
            ProgressSurfaceMiniRadio.IsChecked = _progressEditSurface == ProgressSurface.Mini;
            ProgressSurfacePanel.Visibility = perWindow ? Visibility.Visible : Visibility.Collapsed;
            ProgressPreviewMaterial.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.Progress);
            LoadProgressAppearanceControls();
        }
        finally
        {
            _progressEditorLoading = false;
        }

        _progressSaveTimer.Tick += (_, _) =>
        {
            _progressSaveTimer.Stop();
            FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        };

        // Предпросмотр следует за прокруткой, пока карточка на экране (UpdateProgressPreviewSticky).
        PART_ContentScroll.ScrollChanged += (_, _) => UpdateProgressPreviewSticky();
        ProgressAppearanceCard.SizeChanged += (_, _) => UpdateProgressPreviewSticky();
        ProgressPreviewPanel.SizeChanged += (_, _) => UpdateProgressPreviewSticky();
        ProgressAppearanceCard.IsVisibleChanged += (_, _) => UpdateProgressPreviewSticky();

        UpdateProgressPreview();
    }

    // Показывает в контролах значения редактируемого окна (при смене окна, вида или сбросе) и скрывает то, что к выбранному
    // виду полосы не относится: так нет настроек, которые ничего не меняют.
    private void LoadProgressAppearanceControls()
    {
        ProgressBarAppearance appearance = EditedProgressAppearance;
        ProgressSurface surface = EditedProgressSurface;
        string style = ProgressStyleResolver.Effective(_settings, surface);
        bool perWindow = _settings.ProgressAppearanceScope == "PerWindow";
        bool followMain = surface == ProgressSurface.NowPlaying && perWindow && string.IsNullOrEmpty(_settings.NowPlayingProgressStyle);

        ProgressStyleFollowMainRadio.Visibility = surface == ProgressSurface.NowPlaying && perWindow ? Visibility.Visible : Visibility.Collapsed;
        ProgressStyleWaveformRadio.Visibility = ProgressStyleResolver.SupportsWaveform(surface) ? Visibility.Visible : Visibility.Collapsed;
        ProgressStyleFollowMainRadio.IsChecked = followMain;
        ProgressStyleSliderRadio.IsChecked = !followMain && style == "Slider";
        ProgressStyleWaveformRadio.IsChecked = !followMain && style == "Waveform";
        ProgressStyleMaterialRadio.IsChecked = !followMain && style == "Material";
        ProgressStyleMaterialSliderRadio.IsChecked = !followMain && style == "MaterialSlider";

        ProgressColorCoverRadio.IsChecked = appearance.ColorMode == "Cover";
        ProgressColorCustomRadio.IsChecked = appearance.ColorMode == "Custom";
        ProgressColorAccentRadio.IsChecked = appearance.ColorMode is not ("Cover" or "Custom");
        ProgressColorCustomPanel.Visibility = appearance.ColorMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        ProgressShapeFlatRadio.IsChecked = appearance.IsFlat;
        ProgressShapeRoundRadio.IsChecked = !appearance.IsFlat;
        ProgressWaveformCapsulesRadio.IsChecked = appearance.WaveformShape == "Capsules";
        ProgressWaveformWaveRadio.IsChecked = appearance.WaveformShape != "Capsules";
        ProgressWaveAlwaysCheckBox.IsChecked = appearance.WaveAlways;
        ProgressWaveHandleCheckBox.IsChecked = appearance.WaveHandle;
        ProgressHoverTimeCheckBox.IsChecked = appearance.HoverTime;
        ProgressHoverHighlightCheckBox.IsChecked = appearance.HoverHighlight;

        foreach (var binding in ProgressSliders())
        {
            binding.Slider.Value = Math.Clamp(Math.Round(binding.Get(appearance) * 100), binding.Slider.Minimum, binding.Slider.Maximum);
            binding.Text.Text = $"{(int)Math.Round(binding.Slider.Value)}%";
        }

        // Контур вокруг обложки мини-плеера тоже может быть волной, поэтому у мини-плеера волновые настройки показываются, пока
        // контур включён, и подсказка прямо говорит, почему на контуре волны не видно (он выключен или не «Material»).
        bool ringShown = surface == ProgressSurface.Mini && _settings.MiniPlayerShowArtworkProgress;
        bool ringWavy = ringShown && _settings.MiniPlayerArtworkProgressStyle == "Material";
        ProgressWaveGroup.Visibility = style == "Material" || ringShown ? Visibility.Visible : Visibility.Collapsed;
        ProgressRingNote.Visibility = ringShown ? Visibility.Visible : Visibility.Collapsed;
        ProgressRingNote.Text = LocalizationService.Translate(ringWavy
            ? "Контур вокруг обложки: его вид и толщина выбираются на вкладке «Мини-плеер», а волна и цвет берутся отсюда."
            : "Контур вокруг обложки сейчас не волнистый (вид «Обычный» или «Material Slider»), поэтому волну на нём не видно.");
        ProgressRingMakeWavyButton.Visibility = ringShown && !ringWavy ? Visibility.Visible : Visibility.Collapsed;
        ProgressWaveformGroup.Visibility = style == "Waveform" ? Visibility.Visible : Visibility.Collapsed;

        // Waveform рисует только главное окно: если он выбран там, а у этого окна наследуется, объясняем, что будет обычная полоса.
        bool inheritsWaveform = surface != ProgressSurface.Main
            && ProgressStyleResolver.Canonical(_settings.ProgressBarStyle) == "Waveform"
            && (!perWindow || followMain);
        ProgressStyleNote.Text = LocalizationService.Translate(
            "Waveform есть только в главном окне: в Now Playing и мини-плеере вместо него показывается обычная полоса.");
        ProgressStyleNote.Visibility = inheritsWaveform ? Visibility.Visible : Visibility.Collapsed;
    }

    // Применяет профиль к настоящим окнам и предпросмотру; запись в файл откладывается, чтобы не писать на каждый шаг ползунка.
    private void CommitProgressAppearance(bool saveNow = false)
    {
        UpdateProgressPreview();
        _owner.ApplyProgressAppearance();

        _progressSaveTimer.Stop();
        if (saveNow)
            FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        else
            _progressSaveTimer.Start();
    }

    // Вызывается главным окном при смене трека, обложки, акцента и формы звука: предпросмотр должен показывать то же, что окна.
    public void RefreshProgressPreview()
    {
        if (_progressEditorLoading) return;

        UpdateProgressPreview();
    }

    private void UpdateProgressPreview()
    {
        ProgressBarAppearance appearance = EditedProgressAppearance;
        ProgressSurface surface = EditedProgressSurface;
        string style = ProgressStyleResolver.Effective(_settings, surface);
        SolidColorBrush brush = ProgressAppearanceApplier.ToBrush(_owner.GetProgressBarColor(surface));

        // Показываем только вид, который у выбранного окна на самом деле стоит, и в ширину настоящей полосы этого окна.
        ProgressPreviewHost.Width = surface switch
        {
            ProgressSurface.NowPlaying => 400.0,
            ProgressSurface.Mini => 200.0,
            _ => 390.0
        };
        ProgressPreviewWaveformHost.Visibility = style == "Waveform" ? Visibility.Visible : Visibility.Collapsed;
        ProgressPreviewMaterial.Visibility = style == "Material" ? Visibility.Visible : Visibility.Collapsed;
        ProgressPreviewMaterialSlider.Visibility = style == "MaterialSlider" ? Visibility.Visible : Visibility.Collapsed;
        ProgressPreviewPlain.Visibility = style == "Slider" ? Visibility.Visible : Visibility.Collapsed;

        string caption = LocalizationService.Translate("Предпросмотр");
        if (_settings.ProgressAppearanceScope == "PerWindow")
        {
            string window = surface switch
            {
                ProgressSurface.NowPlaying => "Now Playing",
                ProgressSurface.Mini => LocalizationService.Translate("Мини-плеер"),
                _ => LocalizationService.Translate("Главное окно")
            };
            caption = $"{caption} — {window}";
        }

        ProgressPreviewCaption.Text = caption;

        ProgressAppearanceApplier.Apply(ProgressPreviewWaveform, appearance);
        ProgressAppearanceApplier.Apply(ProgressPreviewMaterial, appearance);
        ProgressAppearanceApplier.Apply(ProgressPreviewMaterialSlider, appearance);
        ProgressPreviewWaveform.PlayedBrush = brush;
        ProgressPreviewMaterial.PlayedBrush = brush;
        ProgressPreviewMaterialSlider.ActiveBrush = brush;
        ProgressPreviewFill.Background = brush;

        // Форма звука текущего трека, если она уже посчитана; иначе образец с тем же числом столбиков, что у настоящей.
        ProgressPreviewWaveform.Peaks = _owner.GetCurrentWaveformPeaks() ?? _samplePeaks ?? (_samplePeaks = CreateSamplePeaks());

        double height = 4.0 * appearance.TrackThickness;
        var radius = new CornerRadius(appearance.IsFlat ? 0.0 : height / 2);
        ProgressPreviewTrack.Height = height;
        ProgressPreviewFill.Height = height;
        ProgressPreviewTrack.CornerRadius = radius;
        ProgressPreviewFill.CornerRadius = radius;

        System.Windows.Media.Color swatch = ToastColorCalculator.TryParse(appearance.ColorHex)
            ?? System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
        ProgressColorSwatch.Background = new SolidColorBrush(swatch);

        UpdateProgressPreviewSticky();
    }

    // Предпросмотр остаётся на экране, пока виден хотя бы его кусок карточки: сдвигается вниз вместе с прокруткой, но не дальше
    // нижнего края карточки (за её пределы настройки уже другие, предпросмотр им не нужен).
    private void UpdateProgressPreviewSticky()
    {
        if (!ProgressAppearanceCard.IsVisible || !ProgressPreviewPanel.IsVisible || ProgressPreviewPanel.ActualHeight <= 0)
        {
            ProgressPreviewTranslate.Y = 0;
            return;
        }

        try
        {
            Point panelTop = ProgressPreviewPanel.TransformToAncestor(PART_ContentScroll).Transform(new Point(0, 0));
            Point cardTop = ProgressAppearanceCard.TransformToAncestor(PART_ContentScroll).Transform(new Point(0, 0));

            const double topGap = 8.0;
            const double bottomPadding = 16.0;
            double naturalTop = panelTop.Y - ProgressPreviewTranslate.Y;
            double cardBottom = cardTop.Y + ProgressAppearanceCard.ActualHeight;
            double maxOffset = cardBottom - bottomPadding - (naturalTop + ProgressPreviewPanel.ActualHeight);
            double wanted = topGap - naturalTop;

            ProgressPreviewTranslate.Y = Math.Max(0.0, Math.Min(wanted, maxOffset));
        }
        catch (InvalidOperationException)
        {
            ProgressPreviewTranslate.Y = 0;
        }
    }

    private float[]? _samplePeaks;

    // Образец формы звука: 300 столбиков, как у WaveformGenerator.BucketCount, с «живыми» перепадами громкости; детерминированный,
    // чтобы вид не прыгал между открытиями окна.
    private static float[] CreateSamplePeaks()
    {
        var random = new Random(42);
        var peaks = new float[WaveformGenerator.BucketCount];
        double level = 0.5;
        for (int i = 0; i < peaks.Length; i++)
        {
            level = level * 0.6 + random.NextDouble() * 0.4;
            double section = 0.65 + 0.35 * Math.Sin(i * 0.045);
            peaks[i] = (float)Math.Clamp(0.12 + level * section * 0.95, 0.1, 1.0);
        }

        return peaks;
    }

    private void SetEditedProgressAppearance(ProgressBarAppearance appearance)
    {
        if (_settings.ProgressAppearanceScope != "PerWindow")
            _settings.ProgressAppearanceMain = appearance;
        else if (_progressEditSurface == ProgressSurface.NowPlaying)
            _settings.ProgressAppearanceNowPlaying = appearance;
        else if (_progressEditSurface == ProgressSurface.Mini)
            _settings.ProgressAppearanceMini = appearance;
        else
            _settings.ProgressAppearanceMain = appearance;
    }

    private static bool IsDefaultAppearance(ProgressBarAppearance appearance) =>
        JsonSerializer.Serialize(appearance) == JsonSerializer.Serialize(new ProgressBarAppearance());

    private void ReloadProgressControls()
    {
        _progressEditorLoading = true;
        try
        {
            LoadProgressAppearanceControls();
        }
        finally
        {
            _progressEditorLoading = false;
        }
    }

    private void ApplyProgressStylesToWindows()
    {
        _owner.ApplyProgressBarStyle();
        _owner.ApplyMiniPlayerProgressBarVisibilityLive();
    }

    private void ProgressScopeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        bool perWindow = ProgressScopePerWindowRadio.IsChecked == true;
        _settings.ProgressAppearanceScope = perWindow ? "PerWindow" : "Shared";
        if (perWindow)
        {
            // Чтобы переход к отдельным настройкам ничего не менял на глаз, окна без своих настроек начинают с общего вида.
            if (IsDefaultAppearance(_settings.ProgressAppearanceNowPlaying))
                _settings.ProgressAppearanceNowPlaying = _settings.ProgressAppearanceMain.Clone();
            if (IsDefaultAppearance(_settings.ProgressAppearanceMini))
                _settings.ProgressAppearanceMini = _settings.ProgressAppearanceMain.Clone();
        }

        ProgressSurfacePanel.Visibility = perWindow ? Visibility.Visible : Visibility.Collapsed;
        ReloadProgressControls();
        ApplyProgressStylesToWindows();
        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressSurfaceRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        _progressEditSurface = ProgressSurfaceNowPlayingRadio.IsChecked == true ? ProgressSurface.NowPlaying
            : ProgressSurfaceMiniRadio.IsChecked == true ? ProgressSurface.Mini
            : ProgressSurface.Main;
        ReloadProgressControls();
        UpdateProgressPreview();
    }

    private void ProgressStyleRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        bool follow = ProgressStyleFollowMainRadio.IsChecked == true;
        string style = ProgressStyleWaveformRadio.IsChecked == true ? "Waveform"
            : ProgressStyleMaterialRadio.IsChecked == true ? "Material"
            : ProgressStyleMaterialSliderRadio.IsChecked == true ? "MaterialSlider"
            : "Slider";
        ProgressStyleResolver.Set(_settings, EditedProgressSurface, style, follow);

        ReloadProgressControls();
        ApplyProgressStylesToWindows();
        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressColorRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        ProgressBarAppearance appearance = EditedProgressAppearance;
        appearance.ColorMode = ProgressColorCoverRadio.IsChecked == true ? "Cover"
            : ProgressColorCustomRadio.IsChecked == true ? "Custom"
            : "Accent";
        ProgressColorCustomPanel.Visibility = appearance.ColorMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressColorPickButton_Click(object sender, RoutedEventArgs e)
    {
        ProgressBarAppearance appearance = EditedProgressAppearance;
        System.Windows.Media.Color initial = ToastColorCalculator.TryParse(appearance.ColorHex)
            ?? System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(initial.R, initial.G, initial.B),
            FullOpen = true
        };

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new Wpf32Window(handle)) != System.Windows.Forms.DialogResult.OK) return;

        var picked = dialog.Color;
        appearance.ColorHex = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressShapeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        EditedProgressAppearance.EndShape = ProgressShapeFlatRadio.IsChecked == true ? "Flat" : "Round";
        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressWaveformShapeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        EditedProgressAppearance.WaveformShape = ProgressWaveformCapsulesRadio.IsChecked == true ? "Capsules" : "Wave";
        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        ProgressBarAppearance appearance = EditedProgressAppearance;
        appearance.WaveAlways = ProgressWaveAlwaysCheckBox.IsChecked == true;
        appearance.WaveHandle = ProgressWaveHandleCheckBox.IsChecked == true;
        appearance.HoverTime = ProgressHoverTimeCheckBox.IsChecked == true;
        appearance.HoverHighlight = ProgressHoverHighlightCheckBox.IsChecked == true;
        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressAppearanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_progressEditorLoading) return;

        var slider = (Slider)sender;
        foreach (var binding in ProgressSliders())
        {
            if (!ReferenceEquals(binding.Slider, slider)) continue;

            binding.Text.Text = $"{(int)Math.Round(slider.Value)}%";
            binding.Set(EditedProgressAppearance, slider.Value / 100.0);
            break;
        }

        CommitProgressAppearance();
    }

    // Включает контур в виде «Material» через те же переключатели, что на вкладке «Мини-плеер»: их обработчики применяют вид к окну.
    private void ProgressRingMakeWavyButton_Click(object sender, RoutedEventArgs e)
    {
        MiniShowArtworkProgressCheckBox.IsChecked = true;
        MiniArtworkProgressStyleMaterialRadio.IsChecked = true;
        ReloadProgressControls();
        UpdateProgressPreview();
    }

    private void ProgressAppearanceResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetEditedProgressAppearance(new ProgressBarAppearance());
        ReloadProgressControls();
        CommitProgressAppearance(saveNow: true);
    }

    // Предпросмотр повторяет подсказку главного окна: подсветка до курсора и время (длительность образца 3:30).
    private void ProgressPreviewWaveform_MouseMove(object sender, MouseEventArgs e)
    {
        ProgressBarAppearance appearance = EditedProgressAppearance;
        double width = ProgressPreviewWaveformHost.ActualWidth;
        if (width <= 0) return;

        double ratio = Math.Clamp(e.GetPosition(ProgressPreviewWaveformHost).X / width, 0.0, 1.0);
        ProgressPreviewWaveform.HoverRatio = appearance.HoverHighlight ? ratio : null;

        var time = TimeSpan.FromSeconds(210 * ratio);
        ProgressPreviewWaveformHost.ToolTip = appearance.HoverTime ? time.ToString(@"mm\:ss") : null;
    }

    private void ProgressPreviewWaveform_MouseLeave(object sender, MouseEventArgs e)
    {
        ProgressPreviewWaveform.HoverRatio = null;
        ProgressPreviewWaveformHost.ToolTip = null;
    }
}
