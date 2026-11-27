using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumisense;

// Выбор фона Now Playing (AppSettings.NowPlayingBackground): "Clouds" (по умолчанию), "Orbs", "Waves" — движущиеся фоны;
// "Cover" — размытая обложка, "Solid" — цвет NowPlayingBackgroundColor, "None" — простой тёмный фон.
public partial class NowPlayingWindow
{
    private const string OrbsBackgroundMode = "Orbs";
    private const string WavesBackgroundMode = "Waves";
    private const string CoverBackgroundMode = "Cover";
    private const string SolidBackgroundMode = "Solid";
    private const string NoneBackgroundMode = "None";
    private const string PlainBackdropColor = "#0F1115";

    // Любое неизвестное значение трактуется как «Облака».
    private string BackgroundMode => _owner.Settings.NowPlayingBackground is OrbsBackgroundMode or WavesBackgroundMode
        or CoverBackgroundMode or SolidBackgroundMode or NoneBackgroundMode
        ? _owner.Settings.NowPlayingBackground : "Clouds";

    private bool IsOrbsBackground => BackgroundMode == OrbsBackgroundMode;

    private bool IsWavesBackground => BackgroundMode == WavesBackgroundMode;

    // Текущая обложка для фона «Размытая обложка»; хранится, чтобы фон можно было перестроить при смене режима.
    private BitmapSource? _backdropArtwork;

    // Применяет выбранный режим фона. Вызывается при создании окна и из MainWindow при смене настройки.
    internal void ApplyBackgroundMode()
    {
        string mode = BackgroundMode;
        CloudsBackdrop.Visibility = mode == "Clouds" ? Visibility.Visible : Visibility.Collapsed;
        OrbsBackdrop.Visibility = mode == OrbsBackgroundMode ? Visibility.Visible : Visibility.Collapsed;
        WavesBackdrop.Visibility = mode == WavesBackgroundMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateStaticBackdrops();

        if (mode == OrbsBackgroundMode) EnsureOrbs();
        ApplyDynamicPalette(_dynamicPalette, animate: false);

        UpdateBackdropGeometry();
        // Таймер облаков нужен только режиму «Облака»; шары и волны двигаются своим покадровым расчётом.
        UpdateAmbientAnimation(_owner.IsPlayingNow);
    }

    // Обложка сменилась: фон «Размытая обложка» перестраивается.
    private void SetBackdropArtwork(BitmapSource? artwork)
    {
        _backdropArtwork = artwork;
        if (BackgroundMode == CoverBackgroundMode) UpdateStaticBackdrops();
    }

    private void UpdateStaticBackdrops()
    {
        string mode = BackgroundMode;
        BitmapSource? blurred = mode == CoverBackgroundMode ? CreateBlurredBackdrop(_backdropArtwork) : null;
        CoverBackdrop.Source = blurred;
        CoverBackdrop.Visibility = blurred is null ? Visibility.Collapsed : Visibility.Visible;

        // Без обложки фон «Размытая обложка» показывает простой тёмный фон, а не пустое место.
        bool solid = mode is SolidBackgroundMode or NoneBackgroundMode || (mode == CoverBackgroundMode && blurred is null);
        SolidBackdrop.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
        if (solid)
        {
            string hex = mode == SolidBackgroundMode ? _owner.Settings.NowPlayingBackgroundColor : PlainBackdropColor;
            SolidBackdrop.Background = new SolidColorBrush(ParseBackdropColor(hex));
        }
    }

    private static Color ParseBackdropColor(string? hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex ?? PlainBackdropColor) is Color color) return color;
        }
        catch (FormatException)
        {
            // Повреждённое значение в настройках: ниже берётся тёмный фон по умолчанию.
        }

        return (Color)ColorConverter.ConvertFromString(PlainBackdropColor)!;
    }

    private static BitmapSource? CreateBlurredBackdrop(BitmapSource? artwork) => BackdropBlur.Create(artwork);
}
