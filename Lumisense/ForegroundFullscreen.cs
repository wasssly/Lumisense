using System.Runtime.InteropServices;
using System.Text;

namespace Lumisense;

// Определяет, что чужое окно на переднем плане закрывает весь монитор окна `self` (игра, видео, F11 в браузере).
// Развёрнутое обычное окно с рамкой и заголовком полноэкранным не считается.
internal static class ForegroundFullscreen
{
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_MAXIMIZE = 0x01000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public static bool CoversMonitorOf(IntPtr self)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == self || foreground == GetDesktopWindow() || foreground == GetShellWindow())
            return false;
        if (!IsWindowVisible(foreground) || IsIconic(foreground)) return false;

        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == Environment.ProcessId) return false;

        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (Array.IndexOf(ShellClasses, className.ToString()) >= 0) return false;

        int style = GetWindowLong(foreground, GWL_STYLE);
        bool framed = (style & WS_CAPTION) == WS_CAPTION || (style & WS_THICKFRAME) != 0;
        if ((style & WS_MAXIMIZE) != 0 && framed) return false;

        IntPtr monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        if (monitor != MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST)) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(foreground, out RECT window)) return false;

        RECT screen = info.rcMonitor;
        return window.Left <= screen.Left && window.Top <= screen.Top && window.Right >= screen.Right && window.Bottom >= screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
