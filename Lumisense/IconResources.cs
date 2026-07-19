namespace AudioPlayer;

internal static class IconResources
{
    // Создаёт новый экземпляр SVG-иконки по её ключу (имени файла в папке Icons/, без расширения).
    // Нужен новый экземпляр SvgPathIcon под каждое присваивание — как и любой FrameworkElement,
    // он не может одновременно быть визуальным потомком двух разных мест (например, кнопки
    // в MainWindow и в MiniPlayerWindow одновременно).
    //
    // Размер, если не передан явно, SvgPathIcon сам возьмёт из ресурса "{resourceKey}DefaultSize" —
    // он объявлен в том же файле Icons/{resourceKey}.xaml, что и сама иконка.
    public static SvgPathIcon Make(string resourceKey, double size = double.NaN) => new()
    {
        Icon = resourceKey,
        Size = size
    };

    // У ui:Button (WPF-UI) при Appearance="Primary" в фон подставляется акцентный цвет, но сама
    // иконка (свойство Icon) при этом НЕ перекрашивается автоматически в контрастный цвет — она
    // продолжает наследовать обычный Foreground (тёмный/светлый в зависимости от темы), из-за чего
    // на ярком акцентном фоне иконка становится плохо видна.
    //
    // Раньше здесь стоял DynamicResource "TextOnAccentFillColorPrimaryBrush" — но это ресурс
    // WPF-UI, который САМ решает, чёрный он или белый, в зависимости от яркости акцентного цвета
    // (см. документацию: "Text colors automatically switch between black and white when accent
    // brightness exceeds 80% HSV"). Именно поэтому иконка оставалась тёмной — при светло-синем
    // системном акценте библиотека сознательно выбирала чёрный текст "для читаемости". Чтобы
    // иконка гарантированно была БЕЛОЙ, а не той, что выберет библиотека, ставим цвет напрямую.
    public static void SetOnAccent(SvgPathIcon icon, bool onAccent)
    {
        if (onAccent)
            icon.Foreground = System.Windows.Media.Brushes.White;
        else
            icon.ClearValue(SvgPathIcon.ForegroundProperty);
    }

    // То же самое, что Make(...), но сразу для иконки внутри постоянно акцентной (Appearance="Primary")
    // кнопки — например, кнопки Пуск/Пауза, которая всегда синяя. Используется вместо Make(...) там,
    // где иконка каждый раз пересоздаётся заново (см. PlayPauseButton.Icon = ...), чтобы не забывать
    // проставлять цвет отдельной строкой на каждом месте вызова.
    public static SvgPathIcon MakeOnAccent(string resourceKey, double size = double.NaN)
    {
        var icon = Make(resourceKey, size);
        SetOnAccent(icon, true);
        return icon;
    }
}
