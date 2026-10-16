using System.Drawing;
using System.Runtime.InteropServices;

namespace Lumisense;

internal readonly record struct ToastPlacement(int X, int Y, int Width, int Height);

// Результат ручного выбора положения: монитор, на котором оказалась карточка, и доли свободной области (см. ToCustomFractions).
public readonly record struct ToastPositionPickResult(string MonitorDeviceName, double X, double Y);

// Расчёт намеренно не зависит от WPF: working area Screen уже задаётся в физических пикселях,
// поэтому помощник можно проверить unit-тестами для каждого угла и масштаба монитора.
internal static class ToastPlacementCalculator
{
    public const int ScreenMarginPixels = 20;

    public static ToastPlacement Calculate(Rectangle workingArea, double widthDip, double heightDip,
        double dpiScale, string position, double customX = 0, double customY = 0)
    {
        double normalizedScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        int width = Math.Max(1, (int)Math.Round(widthDip * normalizedScale));
        int height = Math.Max(1, (int)Math.Round(heightDip * normalizedScale));

        int left = workingArea.Left + ScreenMarginPixels;
        int right = workingArea.Right - width - ScreenMarginPixels;
        int top = workingArea.Top + ScreenMarginPixels;
        int bottom = workingArea.Bottom - height - ScreenMarginPixels;
        int center = workingArea.Left + (workingArea.Width - width) / 2;

        return position switch
        {
            "TopLeft" => new ToastPlacement(left, top, width, height),
            "TopCenter" => new ToastPlacement(center, top, width, height),
            "TopRight" => new ToastPlacement(right, top, width, height),
            "BottomLeft" => new ToastPlacement(left, bottom, width, height),
            "BottomCenter" => new ToastPlacement(center, bottom, width, height),
            "Custom" => new ToastPlacement(
                workingArea.Left + (int)Math.Round(Fraction(customX) * Math.Max(0, workingArea.Width - width)),
                workingArea.Top + (int)Math.Round(Fraction(customY) * Math.Max(0, workingArea.Height - height)),
                width, height),
            _ => new ToastPlacement(right, bottom, width, height),
        };
    }

    // Обратная операция для "Custom": по месту, куда пользователь перетащил карточку, находим доли свободной области.
    public static (double X, double Y) ToCustomFractions(Rectangle workingArea, int x, int y, int width, int height)
    {
        int freeWidth = workingArea.Width - width;
        int freeHeight = workingArea.Height - height;
        return (
            freeWidth > 0 ? Math.Clamp((x - workingArea.Left) / (double)freeWidth, 0, 1) : 0,
            freeHeight > 0 ? Math.Clamp((y - workingArea.Top) / (double)freeHeight, 0, 1) : 0);
    }

    private static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}

internal static class ToastMonitorDpi
{
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;
    private const uint DefaultDpi = 96;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WindowSnapHelper.POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public static double GetScale(System.Windows.Forms.Screen screen, double fallbackScale)
    {
        var area = screen.WorkingArea;
        var point = new WindowSnapHelper.POINT
        {
            X = area.Left + area.Width / 2,
            Y = area.Top + area.Height / 2,
        };
        IntPtr monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
            return dpiX / (double)DefaultDpi;

        return double.IsFinite(fallbackScale) && fallbackScale > 0 ? fallbackScale : 1.0;
    }
}
