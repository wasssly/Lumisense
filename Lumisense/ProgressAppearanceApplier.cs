using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfSlider = System.Windows.Controls.Slider;

namespace Lumisense;

public enum ProgressSurface { Main, NowPlaying, Mini }

// Выбирает профиль вида полосы для окна и переносит его на нарисованные контролы (MaterialProgressView, MaterialSliderVisual,
// WaveformView, дорожка главного ползунка). Общая логика для главного окна, Now Playing, мини-плеера и предпросмотра в настройках.
internal static class ProgressAppearanceApplier
{
    public static ProgressBarAppearance For(AppSettings settings, ProgressSurface surface)
    {
        ProgressBarAppearance shared = settings.ProgressAppearanceMain ?? new ProgressBarAppearance();
        if (settings.ProgressAppearanceScope != "PerWindow") return shared;

        return surface switch
        {
            ProgressSurface.NowPlaying => settings.ProgressAppearanceNowPlaying ?? shared,
            ProgressSurface.Mini => settings.ProgressAppearanceMini ?? shared,
            _ => shared
        };
    }

    public static bool AnyUsesCover(AppSettings settings) =>
        For(settings, ProgressSurface.Main).ColorMode == "Cover"
        || For(settings, ProgressSurface.NowPlaying).ColorMode == "Cover"
        || For(settings, ProgressSurface.Mini).ColorMode == "Cover";

    // cover — цвет обложки текущего трека (null, если его не получилось извлечь): тогда остаётся акцент.
    public static Color ResolveColor(ProgressBarAppearance appearance, Color accent, Color? cover) => appearance.ColorMode switch
    {
        "Cover" => cover ?? accent,
        "Custom" => ToastColorCalculator.TryParse(appearance.ColorHex) ?? accent,
        _ => accent
    };

    public static SolidColorBrush ToBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static void Apply(MaterialProgressView view, ProgressBarAppearance appearance)
    {
        view.ThicknessScale = appearance.TrackThickness;
        view.IsFlat = appearance.IsFlat;
        view.AmplitudeScale = appearance.WaveAmplitude;
        view.WavelengthScale = appearance.WaveLength;
        view.SpeedScale = appearance.WaveSpeed;
        view.AlwaysWave = appearance.WaveAlways;
    }

    public static void Apply(MaterialSliderVisual view, ProgressBarAppearance appearance)
    {
        view.ThicknessScale = appearance.TrackThickness;
        view.HandleScale = appearance.ThumbSize;
        view.IsFlat = appearance.IsFlat;
    }

    public static void Apply(WaveformView view, ProgressBarAppearance appearance)
    {
        view.GapRatio = appearance.WaveformGap;
        view.UnplayedOpacity = appearance.WaveformUnplayedOpacity;
        view.IsFlat = appearance.IsFlat;
    }

    // Обычный ползунок главного окна: толщину и концы рисует PART_TrackRenderer, размер ручки задаёт LegacyThumb, цвет — ресурс
    // акцента на самом Slider (его читают и заливка, и ручка шаблона), поэтому остальные ползунки не затрагиваются.
    public static void Apply(WpfSlider slider, ProgressBarAppearance appearance, Color color)
    {
        slider.ApplyTemplate();
        if (slider.Template?.FindName("PART_TrackRenderer", slider) is MainWindowSliderTrackRenderer renderer)
        {
            renderer.TrackThicknessScale = appearance.TrackThickness;
            renderer.IsFlat = appearance.IsFlat;
        }

        if (slider.Template?.FindName("LegacyThumb", slider) is System.Windows.Controls.Primitives.Thumb thumb)
        {
            thumb.Width = BaseThumbSize * appearance.ThumbSize;
            thumb.Height = BaseThumbSize * appearance.ThumbSize;
        }

        if (slider.Template?.FindName("PART_MaterialVisual", slider) is MaterialSliderVisual material)
            Apply(material, appearance);

        // Material-вид ползунка (PART_MaterialVisual) берёт ActiveBrush из того же ресурса, поэтому красится вместе с обычным.
        if (appearance.ColorMode == "Accent")
            slider.Resources.Remove("AccentFillColorDefaultBrush");
        else
            slider.Resources["AccentFillColorDefaultBrush"] = ToBrush(color);
    }

    // Размер ручки обычного ползунка в шаблоне MainWindowPlaybackSliderStyle.
    private const double BaseThumbSize = 14.0;
}
