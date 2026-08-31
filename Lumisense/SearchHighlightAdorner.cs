using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumisense;

// Временная подсветка вокруг элемента настройки, к которому переходят из результатов поиска
// (SettingsWindow.SearchResultItem_Click). Полупрозрачный скруглённый прямоугольник,
// рисуется через AdornerLayer, разметку самого элемента не трогает.
public sealed class SearchHighlightAdorner : Adorner
{
    private readonly Border _visual;

    private SearchHighlightAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;
        _visual = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(90, 96, 165, 250)),
            CornerRadius = new CornerRadius(6)
        };
        AddVisualChild(_visual);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    protected override Size MeasureOverride(Size constraint)
    {
        _visual.Measure(constraint);
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = AdornedElement.RenderSize.Width;
        double height = AdornedElement.RenderSize.Height;
        _visual.Arrange(new Rect(-8, -6, width + 16, height + 12));
        return finalSize;
    }

    // Подсвечивает target и плавно убирает подсветку через ~секунду.
    // Если у элемента ещё нет AdornerLayer (не отображён на экране) — просто ничего не делает.
    public static void Flash(FrameworkElement target)
    {
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer == null) return;

        var adorner = new SearchHighlightAdorner(target);
        layer.Add(adorner);

        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(900))
        {
            BeginTime = TimeSpan.FromMilliseconds(350)
        };
        fade.Completed += (_, _) => layer.Remove(adorner);
        adorner.BeginAnimation(OpacityProperty, fade);
    }
}
