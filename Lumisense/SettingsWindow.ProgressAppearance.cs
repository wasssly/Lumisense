using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Карточка «Вид полосы воспроизведения» (AppSettings.ProgressAppearance*, ProgressBarAppearance). Редактирует профиль общий
// или одного из окон; предпросмотр рисуют настоящие WaveformView, MaterialProgressView и MaterialSliderVisual.
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
            ProgressPreviewWaveform.Peaks = CreateSamplePeaks();
            ProgressPreviewMaterial.IsAnimationEnabled = !AccessibilityPreferences.ShouldReduceMotion(_settings);
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
        UpdateProgressPreview();
    }

    // Показывает в контролах значения редактируемого профиля (при смене окна или сбросе).
    private void LoadProgressAppearanceControls()
    {
        ProgressBarAppearance appearance = EditedProgressAppearance;

        ProgressColorCoverRadio.IsChecked = appearance.ColorMode == "Cover";
        ProgressColorCustomRadio.IsChecked = appearance.ColorMode == "Custom";
        ProgressColorAccentRadio.IsChecked = appearance.ColorMode is not ("Cover" or "Custom");
        ProgressColorCustomPanel.Visibility = appearance.ColorMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        ProgressShapeFlatRadio.IsChecked = appearance.IsFlat;
        ProgressShapeRoundRadio.IsChecked = !appearance.IsFlat;
        ProgressWaveAlwaysCheckBox.IsChecked = appearance.WaveAlways;
        ProgressHoverTimeCheckBox.IsChecked = appearance.HoverTime;
        ProgressHoverHighlightCheckBox.IsChecked = appearance.HoverHighlight;

        foreach (var binding in ProgressSliders())
        {
            binding.Slider.Value = Math.Clamp(Math.Round(binding.Get(appearance) * 100), binding.Slider.Minimum, binding.Slider.Maximum);
            binding.Text.Text = $"{(int)Math.Round(binding.Slider.Value)}%";
        }
    }

    // Применяет профиль к настоящим окнам и предпросмотру; запись в файл откладывается, чтобы не писать на каждый шаг ползунка.
    private void CommitProgressAppearance(bool saveNow = false)
    {
        UpdateProgressPreview();
        _owner.ApplyProgressAppearance();

        if (saveNow)
        {
            _progressSaveTimer.Stop();
            FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        }
        else
        {
            _progressSaveTimer.Stop();
            _progressSaveTimer.Start();
        }
    }

    private void UpdateProgressPreview()
    {
        ProgressBarAppearance appearance = EditedProgressAppearance;
        SolidColorBrush brush = ProgressAppearanceApplier.ToBrush(_owner.GetProgressBarColor(EditedProgressSurface));

        ProgressAppearanceApplier.Apply(ProgressPreviewWaveform, appearance);
        ProgressAppearanceApplier.Apply(ProgressPreviewMaterial, appearance);
        ProgressAppearanceApplier.Apply(ProgressPreviewMaterialSlider, appearance);
        ProgressPreviewWaveform.PlayedBrush = brush;
        ProgressPreviewMaterial.PlayedBrush = brush;
        ProgressPreviewMaterialSlider.ActiveBrush = brush;
        ProgressPreviewFill.Background = brush;

        double height = 4.0 * appearance.TrackThickness;
        var radius = new CornerRadius(appearance.IsFlat ? 0.0 : height / 2);
        ProgressPreviewTrack.Height = height;
        ProgressPreviewFill.Height = height;
        ProgressPreviewTrack.CornerRadius = radius;
        ProgressPreviewFill.CornerRadius = radius;

        System.Windows.Media.Color swatch = ToastColorCalculator.TryParse(appearance.ColorHex)
            ?? System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
        ProgressColorSwatch.Background = new SolidColorBrush(swatch);
    }

    // Образец формы звука для предпросмотра: детерминированный, чтобы вид не прыгал между открытиями окна.
    private static float[] CreateSamplePeaks()
    {
        var peaks = new float[90];
        for (int i = 0; i < peaks.Length; i++)
        {
            double envelope = 0.35 + 0.45 * Math.Abs(Math.Sin(i * 0.21)) + 0.15 * Math.Abs(Math.Sin(i * 0.77));
            peaks[i] = (float)Math.Clamp(envelope, 0.12, 1.0);
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

    private void ProgressScopeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        bool perWindow = ProgressScopePerWindowRadio.IsChecked == true;
        _settings.ProgressAppearanceScope = perWindow ? "PerWindow" : "Shared";
        if (perWindow)
        {
            // Чтобы переход к отдельным профилям ничего не менял на глаз, окна без своих настроек начинают с общего вида.
            if (IsDefaultAppearance(_settings.ProgressAppearanceNowPlaying))
                _settings.ProgressAppearanceNowPlaying = _settings.ProgressAppearanceMain.Clone();
            if (IsDefaultAppearance(_settings.ProgressAppearanceMini))
                _settings.ProgressAppearanceMini = _settings.ProgressAppearanceMain.Clone();
        }

        ProgressSurfacePanel.Visibility = perWindow ? Visibility.Visible : Visibility.Collapsed;
        _progressEditorLoading = true;
        try
        {
            LoadProgressAppearanceControls();
        }
        finally
        {
            _progressEditorLoading = false;
        }

        CommitProgressAppearance(saveNow: true);
    }

    private void ProgressSurfaceRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        _progressEditSurface = ProgressSurfaceNowPlayingRadio.IsChecked == true ? ProgressSurface.NowPlaying
            : ProgressSurfaceMiniRadio.IsChecked == true ? ProgressSurface.Mini
            : ProgressSurface.Main;
        _progressEditorLoading = true;
        try
        {
            LoadProgressAppearanceControls();
        }
        finally
        {
            _progressEditorLoading = false;
        }

        UpdateProgressPreview();
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

    private void ProgressCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_progressEditorLoading) return;

        ProgressBarAppearance appearance = EditedProgressAppearance;
        appearance.WaveAlways = ProgressWaveAlwaysCheckBox.IsChecked == true;
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

    private void ProgressAppearanceResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetEditedProgressAppearance(new ProgressBarAppearance());
        _progressEditorLoading = true;
        try
        {
            LoadProgressAppearanceControls();
        }
        finally
        {
            _progressEditorLoading = false;
        }

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
