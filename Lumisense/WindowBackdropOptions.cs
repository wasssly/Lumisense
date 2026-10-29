using Wpf.Ui.Controls;

namespace Lumisense;

// Значения AppSettings.WindowBackdropType и их соответствие backdrop WPF-UI; одно место вместо копий в каждом окне.
internal static class WindowBackdropOptions
{
    public const string Mica = "Mica";
    public const string MicaAlt = "MicaAlt";
    public const string Acrylic = "Acrylic";

    // Обычное размытие позади окна без тонировки: см. DwmBlurBackdrop.
    public const string Blur = "Blur";

    // DWMSBT_TABBEDWINDOW появился в Windows 11 22H2; на более раннем билде Mica Alt молча не рисуется.
    private const int MicaAltMinimumBuild = 22621;

    public static string Normalize(string? value) => value is Acrylic or MicaAlt or Blur ? value : Mica;

    // Windows 11 начинается с сборки 22000. На Windows 10 выбор основы окна отключён: системного Mica там нет, а Acrylic и «Размытие»
    // дают белые окна или не меняют ничего. Сохранённое значение остаётся в настройках (оно сработает после перехода на Windows 11),
    // но действует всегда Mica.
    private const int SelectionMinimumBuild = 22000;

    public static bool IsSelectionSupportedNow => IsSelectionSupported(Environment.OSVersion.Version.Build);

    internal static bool IsSelectionSupported(int osBuild) => osBuild >= SelectionMinimumBuild;

    internal static string Effective(string? value, int osBuild) => IsSelectionSupported(osBuild) ? Normalize(value) : Mica;

    public static bool IsBlur(string? value) => IsBlur(value, Environment.OSVersion.Version.Build);

    internal static bool IsBlur(string? value, int osBuild) => Effective(value, osBuild) == Blur;

    public static WindowBackdropType ToWpfUi(string? value) => ToWpfUi(value, Environment.OSVersion.Version.Build);

    internal static WindowBackdropType ToWpfUi(string? value, int osBuild) => Effective(value, osBuild) switch
    {
        // Blur держится на Acrylic: он даёт прозрачный фон окна и работает в Windows 10, а размытие накладывает DwmBlurBackdrop.
        Acrylic or Blur => WindowBackdropType.Acrylic,
        MicaAlt when osBuild >= MicaAltMinimumBuild => WindowBackdropType.Tabbed,
        _ => WindowBackdropType.Mica
    };
}
