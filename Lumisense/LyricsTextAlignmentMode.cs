using System.Windows;

namespace Lumisense;

// Значения AppSettings.LyricsTextAlignment и их перевод в TextAlignment; общий для панели текста главного окна и Now Playing.
internal static class LyricsTextAlignmentMode
{
    public const string Left = "Left";
    public const string Center = "Center";
    public const string Right = "Right";

    public static TextAlignment ToTextAlignment(string? mode) => mode switch
    {
        Center => TextAlignment.Center,
        Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };
}
