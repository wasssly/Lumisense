using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lumisense;

// Определение "похоже, сейчас работает игра или её оверлей" — без системных хуков и внешних
// зависимостей, двумя независимыми и по отдельности не идеальными сигналами:
//
// 1) окно переднего плана занимает ровно весь монитор, на котором находится (типичный признак
//    exclusive/borderless-fullscreen игры), и это не рабочий стол/проводник и не сам Lumisense;
// 2) в системе запущен один из известных процессов игрового оверлея (RTSS, Xbox Game Bar,
//    NVIDIA/AMD оверлей и т.п.) — такие процессы почти никогда не работают вне игровой сессии.
//
// Это эвристика, а не официальный API от Windows/Steam/производителей видеокарт — она может и
// пропустить реальную игру (окно в оконном режиме без известного оверлея), и сработать на
// что-то постороннее (полноэкранный плеер, презентация). Поэтому результат используется только
// как необязательная автоматическая подсказка (см. MainWindow.RefreshGameOverlayAutoDetection),
// а не как безусловное решение, и пользователь может отключить автоопределение целиком в
// настройках (AppSettings.GameOverlayCompatibilityAutoDetect).
public static class GameOverlayDetectionService
{
    private static readonly string[] KnownOverlayProcessNames =
    {
        "RTSS",                                            // RivaTuner Statistics Server (в т.ч. оверлей MSI Afterburner)
        "GameBar", "GameBarPresenceWriter", "GameBarFTServer", // Xbox Game Bar (Win+G)
        "GameOverlayUI",                                   // Steam — активная игровая сессия с оверлеем
        "NVIDIA Share", "NVIDIA GeForce Experience",       // NVIDIA overlay/ShadowPlay/instant replay
        "RadeonSoftware",                                  // AMD оверлей
    };

    // Процессы, чьё полноэкранное окно переднего плана заведомо не игра — рабочий стол, шелл,
    // поиск — иначе они регулярно давали бы ложное срабатывание.
    private static readonly HashSet<string> IgnoredForegroundProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "ApplicationFrameHost"
    };

    public static bool IsGameOrOverlayLikelyActive()
    {
        try
        {
            return IsKnownOverlayProcessRunning() || IsForegroundWindowFullscreenGame();
        }
        catch
        {
            // Process API/user32 недоступны, нет прав и т.п. — считаем, что игра не
            // обнаружена, а не роняем таймер обнаружения или приложение целиком.
            return false;
        }
    }

    private static bool IsKnownOverlayProcessRunning()
    {
        foreach (string name in KnownOverlayProcessNames)
        {
            if (Process.GetProcessesByName(name).Length > 0) return true;
        }
        return false;
    }

    private static bool IsForegroundWindowFullscreenGame()
    {
        IntPtr hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return false;

        // Собственные окна Lumisense (включая полноэкранный Now Playing) не считаются игрой —
        // иначе автоопределение включало бы "режим совместимости" от самого себя.
        NativeMethods.GetWindowThreadProcessId(hWnd, out int processId);
        if (processId == Environment.ProcessId) return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (IgnoredForegroundProcessNames.Contains(process.ProcessName)) return false;
        }
        catch
        {
            // Процесс успел завершиться между GetWindowThreadProcessId и GetProcessById —
            // просто продолжаем без имени процесса, геометрии окна достаточно.
        }

        if (!NativeMethods.GetWindowRect(hWnd, out var windowRect)) return false;

        IntPtr monitor = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = NativeMethods.MonitorInfo.Create();
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo)) return false;

        // Допуск в 1 пиксель — на некоторых системах масштабирование DPI даёт округление
        // геометрии окна на пиксель-два относительно границ монитора.
        const int tolerance = 1;
        return Math.Abs(windowRect.Left - monitorInfo.MonitorRect.Left) <= tolerance &&
               Math.Abs(windowRect.Top - monitorInfo.MonitorRect.Top) <= tolerance &&
               Math.Abs(windowRect.Right - monitorInfo.MonitorRect.Right) <= tolerance &&
               Math.Abs(windowRect.Bottom - monitorInfo.MonitorRect.Bottom) <= tolerance;
    }

    private static class NativeMethods
    {
        public const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MonitorInfo
        {
            public int Size;
            public Rect MonitorRect;
            public Rect WorkAreaRect;
            public uint Flags;

            public static MonitorInfo Create() => new() { Size = Marshal.SizeOf<MonitorInfo>() };
        }
    }
}
