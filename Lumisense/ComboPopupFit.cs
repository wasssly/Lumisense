using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumisense;

// Комбобокс подгоняет ширину под самый длинный пункт (в любом языке), а список не шире самого комбобокса.
internal static class ComboPopupFit
{
    // Поля вокруг текста: отступы пункта и рамка списка плюс шеврон закрытого комбобокса.
    private const double ChromeWidth = 64;
    private const double MaxComboWidth = 640;

    private static readonly DataTemplate TrimmedText = CreateTrimmedText();

    private static readonly DependencyProperty BaseMinWidthProperty =
        DependencyProperty.RegisterAttached("BaseMinWidth", typeof(double), typeof(ComboPopupFit), new PropertyMetadata(-1.0));

    // Loaded не всплывает, поэтому слушаем его классовым обработчиком; берём только комбобоксы окна настроек.
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not ComboBox combo || (double)combo.GetValue(BaseMinWidthProperty) >= 0
                || Window.GetWindow(combo) is not SettingsWindow) return;
            combo.SetValue(BaseMinWidthProperty, combo.MinWidth);
            combo.DropDownOpened += (_, _) => Fit(combo);
            combo.Dispatcher.BeginInvoke(() => GrowToItems(combo), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }));
    }

    // После смены языка пересчитываем уже подготовленные комбобоксы сразу, а не при следующем открытии.
    public static void RefreshAll(DependencyObject root)
    {
        if (root is ComboBox combo) GrowToItems(combo);
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
            RefreshAll(VisualTreeHelper.GetChild(root, i));
    }

    private static void Fit(ComboBox combo)
    {
        GrowToItems(combo);
        combo.UpdateLayout();

        if (combo.Template?.FindName("DropDownBorder", combo) is FrameworkElement border && combo.ActualWidth > 0)
            border.MaxWidth = combo.ActualWidth;

        for (int i = 0; i < combo.Items.Count; i++)
        {
            var item = combo.Items[i] as ComboBoxItem ?? combo.ItemContainerGenerator.ContainerFromIndex(i) as ComboBoxItem;
            if (item?.Content is string && item.ContentTemplate is null)
                item.ContentTemplate = TrimmedText;
        }
    }

    private static void GrowToItems(ComboBox combo)
    {
        double baseMin = (double)combo.GetValue(BaseMinWidthProperty);
        if (baseMin < 0 || combo.Items.Count == 0) return;

        var typeface = new Typeface(combo.FontFamily, combo.FontStyle, combo.FontWeight, combo.FontStretch);
        double dip = VisualTreeHelper.GetDpi(combo).PixelsPerDip;
        double widest = 0;
        foreach (object item in combo.Items)
        {
            string? text = item is ComboBoxItem { Content: string s } ? s : item as string;
            if (string.IsNullOrEmpty(text)) continue;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, combo.FontSize, Brushes.Black, dip);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }

        combo.MinWidth = Math.Max(baseMin, Math.Min(MaxComboWidth, Math.Ceiling(widest + ChromeWidth)));
    }

    private static DataTemplate CreateTrimmedText()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new DataTemplate { VisualTree = text };
    }
}
