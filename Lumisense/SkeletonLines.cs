using System.Windows;
using System.Windows.Controls;

namespace Lumisense;

// Скелетон строк текста песни: серые полосы разной длины с бегущим бликом вместо вращающегося индикатора.
public sealed class SkeletonLines : Border
{
    // Доли ширины полос: неровный правый край похож на настоящий текст.
    private static readonly double[] Ratios = [0.92, 0.7, 0.85, 0.55, 0.9, 0.65, 0.8, 0.6];

    public bool IsAnimationEnabled
    {
        get => SkeletonBlock.GetIsAnimationEnabled(this);
        set => SkeletonBlock.SetIsAnimationEnabled(this, value);
    }

    public SkeletonLines()
    {
        IsHitTestVisible = false;
        var panel = new StackPanel();
        foreach (double ratio in Ratios)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
            row.Children.Add(new SkeletonBlock { Height = 12 });
            panel.Children.Add(row);
        }

        Child = panel;
    }
}
