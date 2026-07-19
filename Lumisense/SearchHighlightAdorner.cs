using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioPlayer;

/// <summary>
/// Временная подсветка вокруг элемента настройки, к которому переходят из результатов
/// поиска (см. SettingsWindow.SearchResultItem_Click) — полупрозрачный скруглённый
/// прямоугольник поверх элемента, который плавно исчезает сам. Рисуется через
/// AdornerLayer, поэтому не требует никаких изменений в разметке самого элемента.
/// </summary>
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

    /// <summary>Показывает подсветку поверх <paramref name="target"/> и плавно убирает её
    /// примерно через секунду. Ничего не делает, если у элемента ещё нет AdornerLayer
    /// (например, он не отображён на экране) — тогда просто нет визуального эффекта.</summary>
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
