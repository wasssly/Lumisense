namespace Lumisense;

internal static class IconResources
{
    // Пересчитывается MainWindow.ApplyAccentColor() при каждой смене акцента: белый по умолчанию, но
    // чёрный/белый по яркости при ручном акценте — на светлых пресетах белая иконка плохо видна.
    public static System.Windows.Media.Brush AccentContrastBrush { get; set; } = System.Windows.Media.Brushes.White;

    // Ключ — имя файла в Icons/ без расширения; каждый раз новый экземпляр, т.к. FrameworkElement
    // не может быть в двух местах дерева. Размер по умолчанию — из ресурса "{resourceKey}DefaultSize".
    public static SvgPathIcon Make(string resourceKey, double size = double.NaN) => new()
    {
        Icon = resourceKey,
        Size = size
    };

    // У ui:Button при Appearance="Primary" фон становится акцентным, но Icon сама себя не
    // перекрашивает — виснет на обычном Foreground и на ярком фоне плохо видна.
    public static void SetOnAccent(SvgPathIcon icon, bool onAccent)
    {
        if (onAccent)
            icon.Foreground = AccentContrastBrush;
        else
            icon.ClearValue(SvgPathIcon.ForegroundProperty);
    }

    // Make(...) + сразу контрастный цвет — для иконок в постоянно акцентных кнопках (Пуск/Пауза и т.п.)
    public static SvgPathIcon MakeOnAccent(string resourceKey, double size = double.NaN)
    {
        var icon = Make(resourceKey, size);
        SetOnAccent(icon, true);
        return icon;
    }
}
