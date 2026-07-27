namespace AudioPlayer;

internal static class IconResources
{
    // Ключ — имя файла в Icons/ без расширения. Всегда новый экземпляр SvgPathIcon:
    // FrameworkElement не может одновременно висеть в двух местах визуального дерева.
    // Размер, если не задан, SvgPathIcon возьмёт из ресурса "{resourceKey}DefaultSize".
    public static SvgPathIcon Make(string resourceKey, double size = double.NaN) => new()
    {
        Icon = resourceKey,
        Size = size
    };

    // У ui:Button при Appearance="Primary" фон становится акцентным, но Icon сама себя не
    // перекрашивает — виснет на обычном Foreground и на ярком фоне плохо видна. DynamicResource
    // TextOnAccentFillColorPrimaryBrush тут не спасает: WPF-UI сам решает чёрный/белый по яркости
    // акцента, поэтому на светлых акцентах иконка всё равно оставалась тёмной. Ставим белый напрямую.
    public static void SetOnAccent(SvgPathIcon icon, bool onAccent)
    {
        if (onAccent)
            icon.Foreground = System.Windows.Media.Brushes.White;
        else
            icon.ClearValue(SvgPathIcon.ForegroundProperty);
    }

    // Make(...) + сразу белый цвет — для иконок в постоянно акцентных кнопках (Пуск/Пауза и т.п.)
    public static SvgPathIcon MakeOnAccent(string resourceKey, double size = double.NaN)
    {
        var icon = Make(resourceKey, size);
        SetOnAccent(icon, true);
        return icon;
    }
}
