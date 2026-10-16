using Wpf.Ui.Controls;

namespace Lumisense;

// Значения AppSettings.WindowBackdropType и их соответствие backdrop WPF-UI; одно место вместо копий в каждом окне.
internal static class WindowBackdropOptions
{
    public const string Mica = "Mica";
    public const string MicaAlt = "MicaAlt";
    public const string Acrylic = "Acrylic";

    // DWMSBT_TABBEDWINDOW появился в Windows 11 22H2; на более раннем билде Mica Alt молча не рисуется.
    private const int MicaAltMinimumBuild = 22621;

    public static string Normalize(string? value) => value is Acrylic or MicaAlt ? value : Mica;

    public static WindowBackdropType ToWpfUi(string? value) => ToWpfUi(value, Environment.OSVersion.Version.Build);

    internal static WindowBackdropType ToWpfUi(string? value, int osBuild) => Normalize(value) switch
    {
        Acrylic => WindowBackdropType.Acrylic,
        MicaAlt when osBuild >= MicaAltMinimumBuild => WindowBackdropType.Tabbed,
        _ => WindowBackdropType.Mica
    };
}
