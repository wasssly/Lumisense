using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace AudioPlayer;

/// <summary>
/// Своё окно "Свойства" для обложки трека (по образцу TrackPropertiesWindow) — открывается
/// из контекстного меню по правому клику на обложке в главном окне. Показывает формат,
/// размеры и вес изображения, а также то, из какого трека и как эта обложка получена
/// (тег TagLib, а не отдельный файл на диске — своего пути у неё нет).
/// </summary>
public partial class CoverArtPropertiesWindow : FluentWindow
{
    public CoverArtPropertiesWindow(
        BitmapImage art,
        byte[] artBytes,
        string? mimeType,
        TagLib.PictureType? pictureType,
        string trackTitle,
        string trackArtist,
        string? trackPath)
    {
        InitializeComponent();

        ArtBorder.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };

        TitleText.Text = string.IsNullOrWhiteSpace(trackTitle) || trackTitle == "Файл не выбран"
            ? "Обложка"
            : trackTitle;
        ArtistText.Text = trackArtist == "—" ? "" : trackArtist;
        ArtistText.Visibility = string.IsNullOrWhiteSpace(ArtistText.Text)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

        FormatValue.Text = FormatFromMimeType(mimeType);
        DimensionsValue.Text = $"{art.PixelWidth} × {art.PixelHeight} пикс.";
        SizeValue.Text = FormatFileSize(artBytes.LongLength);
        DpiValue.Text = $"{Math.Round(art.DpiX)} × {Math.Round(art.DpiY)}";

        TrackValue.Text = !string.IsNullOrWhiteSpace(trackPath) ? Path.GetFileName(trackPath) : "—";
        PictureTypeValue.Text = FormatPictureType(pictureType);
    }

    private static string FormatFromMimeType(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/png" => "PNG",
        "image/bmp" => "BMP",
        "image/gif" => "GIF",
        "image/webp" => "WebP",
        "image/jpeg" or "image/jpg" => "JPEG",
        null or "" => "Неизвестно",
        var other => other
    };

    // Тег может помечать картинку не только как "обложка альбома" (самый частый случай),
    // но и как, например, фото исполнителя или логотип группы — показываем это по-русски,
    // а не сырым именем значения перечисления TagLib.
    private static string FormatPictureType(TagLib.PictureType? type) => type switch
    {
        TagLib.PictureType.FrontCover => "Обложка альбома (лицевая)",
        TagLib.PictureType.BackCover => "Обложка альбома (обратная)",
        TagLib.PictureType.Artist => "Фото исполнителя",
        TagLib.PictureType.Media => "Носитель (диск/кассета)",
        TagLib.PictureType.Illustration => "Иллюстрация",
        TagLib.PictureType.NotAPicture => "—",
        null => "—",
        var other => other.ToString()!
    };

    private static string FormatFileSize(long bytes)
    {
        const double mb = 1024 * 1024;
        const double kb = 1024;

        if (bytes >= mb) return $"{bytes / mb:0.0} МБ";
        if (bytes >= kb) return $"{bytes / kb:0.0} КБ";
        return $"{bytes} байт";
    }
}
