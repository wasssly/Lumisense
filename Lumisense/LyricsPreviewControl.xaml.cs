using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumisense;

public partial class LyricsPreviewControl : UserControl
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(142, 142, 142)));
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
        Glow.Opacity = !plain && settings.SyncedLyricsHighlightEffect != "None" ? 0.62 : 0.0;
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
