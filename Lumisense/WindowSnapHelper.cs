using System.Runtime.InteropServices;

namespace AudioPlayer;

// Общая инфраструктура "прилипания к краям экрана" при перетаскивании окна — используется
// мини-плеером (MiniPlayerWindow, см. AppSettings.MiniPlayerSnapToEdges и перехват WM_MOVING в
// нём же). У обычного окна плеера (MainWindow) такой возможности больше нет — перетаскивание
// там идёт через системный ui:TitleBar (HTCAPTION), и попытка примагничивать его к краям экрана
// через WM_MOVING/LocationChanged на практике оказалась ненадёжной, поэтому эту возможность для
// него убрали. WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE/WM_MOVING и RECT/SnapToScreenEdges ниже остаются
// общей инфраструктурой на случай, если понадобятся другому окну — но сейчас их использует
// только мини-плеер.
internal static class WindowSnapHelper
{
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_MOVING = 0x0216;
    public const int WM_EXITSIZEMOVE = 0x0232;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;

    // Дистанция в физических пикселях, на которой окно "магнитится" к краю рабочей области
    // монитора (без учёта панели задач). Работает независимо по X и Y — поэтому окно так же
    // аккуратно прилипает и в углы экрана. Значение специально небольшое, чтобы притяжение
    // ощущалось мягким, а не резким "прыжком" окна к краю.
    public const int SnapMarginPx = 10;

    // Подправляет предложенный Windows прямоугольник окна: если он оказался в пределах
    // SnapMarginPx от какого-либо края рабочей области текущего монитора — ровно к этому краю
    // и прижимаем. Проверяется независимо по горизонтали и вертикали.
    public static void SnapToScreenEdges(ref RECT rect)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        var winBounds = new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
        var workArea = System.Windows.Forms.Screen.FromRectangle(winBounds).WorkingArea;

        if (Math.Abs(rect.Left - workArea.Left) <= SnapMarginPx)
        {
            rect.Left = workArea.Left;
            rect.Right = rect.Left + width;
        }
        else if (Math.Abs(rect.Right - workArea.Right) <= SnapMarginPx)
        {
            rect.Right = workArea.Right;
            rect.Left = rect.Right - width;
        }

        if (Math.Abs(rect.Top - workArea.Top) <= SnapMarginPx)
        {
            rect.Top = workArea.Top;
            rect.Bottom = rect.Top + height;
        }
        else if (Math.Abs(rect.Bottom - workArea.Bottom) <= SnapMarginPx)
        {
            rect.Bottom = workArea.Bottom;
            rect.Top = rect.Bottom - height;
        }
    }
}
