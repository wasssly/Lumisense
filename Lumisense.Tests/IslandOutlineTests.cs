using System.Windows;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// Контур Notch Nook: ровная верхняя сторона на краю экрана и «уши» наружу от прямоугольника.
public sealed class IslandOutlineTests
{
    [Fact]
    public void CreateNotch_ExtendsByEarOnBothSides_AndStartsAtTheTopEdge()
    {
        var rect = new Rect(100, 0, 200, 34);

        Rect bounds = IslandOutline.CreateNotch(rect, 14, 12).Bounds;

        Assert.Equal(88, bounds.Left, 0.5);
        Assert.Equal(312, bounds.Right, 0.5);
        Assert.Equal(0, bounds.Top, 0.5);
        Assert.Equal(34, bounds.Bottom, 0.5);
    }

    [Fact]
    public void CreateNotch_WithoutEarOrRadius_IsPlainRectangle()
    {
        Rect bounds = IslandOutline.CreateNotch(new Rect(10, 0, 100, 40), 0, 0).Bounds;

        Assert.Equal(new Rect(10, 0, 100, 40), bounds);
    }

    [Fact]
    public void CreateNotch_ShrinksEarWhenShapeIsLowerThanRadiusPlusEar()
    {
        // Высота 20: радиус сжимается до 10, и для уха остаётся тоже 10 (а не 12): ухо не заходит ниже начала нижнего скругления.
        Rect bounds = IslandOutline.CreateNotch(new Rect(100, 0, 200, 20), 20, 12).Bounds;

        Assert.Equal(90, bounds.Left, 0.5);
        Assert.Equal(310, bounds.Right, 0.5);
        Assert.Equal(20, bounds.Bottom, 0.5);
    }
}
