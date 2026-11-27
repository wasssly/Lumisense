using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumisense;

public partial class LyricsPreviewControl : UserControl
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(142, 142, 142)));
    private static readonly Brush PillBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x3B, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)));

    public LyricsPreviewControl() => InitializeComponent();

    // Размер, выравнивание, эффект активной строки и режим берутся из тех же настроек, что и в панели текста песни.
    // В обычном режиме строки одинаковые и без подсветки.
    public void Apply(AppSettings settings)
    {
        bool plain = settings.LyricsDisplayMode == "Plain";
        double fontSize = Math.Clamp(settings.SyncedLyricsFontSize, 12, 20);
        TextAlignment alignment = LyricsTextAlignmentMode.ToTextAlignment(settings.LyricsTextAlignment);

        foreach (TextBlock line in new[] { Line1, Line2, Line3 })
        {
            line.FontSize = fontSize;
            line.TextAlignment = alignment;
        }

        Line1.Foreground = plain ? NormalBrush : DimBrush;
        Line2.Foreground = plain ? NormalBrush : Brushes.White;
        Line3.Foreground = plain ? NormalBrush : DimBrush;
        string effect = settings.SyncedLyricsHighlightEffect;
        Glow.BlurRadius = LyricsHighlightStyle.GlowBlurRadius(settings.SyncedLyricsGlowStrength);
        Glow.Opacity = !plain && LyricsHighlightStyle.HasGlow(effect) ? LyricsHighlightStyle.GlowOpacity(settings.SyncedLyricsGlowStrength) : 0.0;
        Pill.Background = !plain && LyricsHighlightStyle.HasPill(effect) ? PillBrush : Brushes.Transparent;
        Bar.Opacity = !plain && LyricsHighlightStyle.HasBar(effect) ? 1.0 : 0.0;

        // Увеличение и приглушение действуют в Now Playing; здесь показываем их так же, чтобы выбор был наглядным.
        double scale = !plain && settings.SyncedLyricsScaleActiveLine ? LyricsHighlightStyle.ScaleFactor(settings.SyncedLyricsScalePercent) : 1.0;
        Line2Scale.ScaleX = scale;
        Line2Scale.ScaleY = scale;
        Line2.RenderTransformOrigin = new Point(alignment switch
        {
            TextAlignment.Center => 0.5,
            TextAlignment.Right => 1.0,
            _ => 0.0,
        }, 0.5);
        bool fade = !plain && settings.SyncedLyricsFadeDistantLines;
        double neighbourOpacity = fade ? 1.0 - 0.5 * Math.Clamp(settings.SyncedLyricsFadeStrength, 0, 100) / 100.0 : 1.0;
        Line1.Opacity = neighbourOpacity;
        Line3.Opacity = neighbourOpacity;

        // В предпросмотре соседние строки размыты вдвое сильнее, чем в окне: иначе на трёх строках разница не видна.
        double previewBlur = !plain && settings.SyncedLyricsBlurInactive
            ? Math.Clamp(settings.SyncedLyricsBlurStrength, LyricsHighlightStyle.MinBlurStrength, LyricsHighlightStyle.MaxBlurStrength) * 0.5
            : 0;
        Line1.Effect = previewBlur > 0 ? new System.Windows.Media.Effects.BlurEffect { Radius = previewBlur } : null;
        Line3.Effect = previewBlur > 0 ? new System.Windows.Media.Effects.BlurEffect { Radius = previewBlur } : null;
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
