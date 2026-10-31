using System.Windows;

namespace Lumisense;

// Размеры окон в XAML рассчитаны на экран 1080p. На низких экранах (например, 1366×768) или при большом масштабе Windows
// такое окно вылезало бы за рабочую область, поэтому размеры при создании ограничиваются её границами.
internal static class WindowSizing
{
    private const double ScreenMargin = 24;

    // Окно меняется, только если оно действительно не помещается: на обычных экранах размеры и ограничения остаются как в XAML.
    // MaxWidth/MaxHeight не трогаем: пользователь должен иметь возможность растянуть окно (в том числе развернуть его).
    public static void FitToWorkArea(Window window)
    {
        Rect area = SystemParameters.WorkArea;
        double maxWidth = Math.Max(320, area.Width - ScreenMargin);
        double maxHeight = Math.Max(320, area.Height - ScreenMargin);

        if (window.MinWidth > maxWidth) window.MinWidth = maxWidth;
        if (window.MinHeight > maxHeight) window.MinHeight = maxHeight;
        if (!double.IsNaN(window.Width) && window.Width > maxWidth) window.Width = maxWidth;
        if (!double.IsNaN(window.Height) && window.Height > maxHeight) window.Height = maxHeight;
    }
}
