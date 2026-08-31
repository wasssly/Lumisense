namespace Lumisense;

internal static class IconResources
{
    // Обновляется MainWindow.ApplyAccentColor() при каждом применении акцента (на старте и
    // при каждой смене в настройках) — белый по умолчанию (исторически так и было, пока
    // акцент был жёстко системным), но пересчитывается на чёрный/белый по формуле яркости,
    // когда акцент выбран вручную (см. AppSettings.AccentColorMode/AccentColorHex): среди
    // пресетов есть и светлые (жёлтый #FFB900, светлый бирюзовый #00B7C3), на которых жёстко
    // белая иконка была бы плохо видна. Раньше пробовали решить это через встроенный
    // DynamicResource TextOnAccentFillColorPrimaryBrush от WPF-UI, но его собственный выбор
    // чёрного/белого не всегда совпадал с ожидаемым на исходном системном акценте — поэтому
    // считаем сами, явно, и полностью управляем результатом.
    public static System.Windows.Media.Brush AccentContrastBrush { get; set; } = System.Windows.Media.Brushes.White;

    // Ключ — имя файла в Icons/ без расширения. Всегда новый экземпляр SvgPathIcon:
    // FrameworkElement не может одновременно висеть в двух местах визуального дерева.
    // Размер, если не задан, SvgPathIcon возьмёт из ресурса "{resourceKey}DefaultSize".
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
