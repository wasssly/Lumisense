using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lumisense;

/// <summary>Перетаскивание дочерних элементов Panel мышью для смены порядка (списки пунктов меню в настройках).
/// Элемент переезжает вслед за курсором, а по окончании вызывается колбэк с идентификаторами (Tag) в новом порядке.</summary>
internal sealed class PanelReorder
{
    private const string DataFormat = "Lumisense.PanelReorderItem";

    private readonly Panel _panel;
    private readonly Action<IReadOnlyList<string>> _changed;
    private Point _pressPoint;
    private UIElement? _pressedItem;

    private PanelReorder(Panel panel, Action<IReadOnlyList<string>> changed)
    {
        _panel = panel;
        _changed = changed;
    }

    public static void Enable(Panel panel, Action<IReadOnlyList<string>> changed)
    {
        var reorder = new PanelReorder(panel, changed);
        panel.AllowDrop = true;
        panel.PreviewMouseLeftButtonDown += reorder.Panel_PreviewMouseLeftButtonDown;
        panel.PreviewMouseMove += reorder.Panel_PreviewMouseMove;
        panel.DragOver += reorder.Panel_DragOver;
        panel.Drop += reorder.Panel_Drop;
    }

    // Приводит порядок элементов панели к заданному списку идентификаторов (кнопка «по умолчанию»).
    public static void Sort(Panel panel, IList<string> order)
    {
        var items = panel.Children.Cast<UIElement>().ToList();
        var arranged = ContextMenuOrdering.Arrange(items, item => (item as FrameworkElement)?.Tag as string, order);
        if (arranged.SequenceEqual(items)) return;

        panel.Children.Clear();
        foreach (UIElement item in arranged) panel.Children.Add(item);
    }

    public static List<string> Ids(Panel panel) =>
        [.. panel.Children.OfType<FrameworkElement>().Select(item => item.Tag as string).OfType<string>()];

    private void Panel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(_panel);
        _pressedItem = FindChild(e.OriginalSource as DependencyObject);
    }

    private void Panel_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem is null) return;

        Point position = e.GetPosition(_panel);
        if (Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance
            && Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        UIElement item = _pressedItem;
        _pressedItem = null;
        // Перетаскиваемая строка бледнеет, чтобы было видно, что именно сейчас переносится.
        item.Opacity = 0.55;
        try
        {
            DragDrop.DoDragDrop(item, new DataObject(DataFormat, item), DragDropEffects.Move);
        }
        finally
        {
            item.Opacity = 1;
        }

        _changed(Ids(_panel));
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormat) || e.Data.GetData(DataFormat) is not UIElement dragged
            || !_panel.Children.Contains(dragged))
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        double y = e.GetPosition(_panel).Y;
        int target = 0;
        foreach (UIElement child in _panel.Children)
        {
            if (child == dragged || child.Visibility == Visibility.Collapsed) continue;
            double center = child.TranslatePoint(new Point(0, child.RenderSize.Height / 2), _panel).Y;
            if (center < y) target++;
        }

        if (_panel.Children.IndexOf(dragged) == target) return;

        _panel.Children.Remove(dragged);
        _panel.Children.Insert(Math.Min(target, _panel.Children.Count), dragged);
        _panel.UpdateLayout();
    }

    private void Panel_Drop(object sender, DragEventArgs e) => e.Handled = true;

    private UIElement? FindChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is UIElement element && _panel.Children.Contains(element)) return element;
            // Источником события может быть не Visual, а элемент текста (Run внутри TextBlock): у него родителя ищем по логическому дереву.
            source = (source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                ? System.Windows.Media.VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
