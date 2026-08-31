using System.Windows.Media;

namespace Lumisense;

// Координирует переиспользуемое toast-окно и выбор экрана. Политика «показывать ли сейчас»
// остаётся в MainWindow, потому что зависит от причины смены трека и состояния playback.
internal sealed class TrackChangeToastController : IDisposable
{
    private TrackChangeToastWindow? _window;

    public void Show(string title, string artist, Brush? art, bool isLightTheme,
        System.Windows.Forms.Screen screen, AppSettings settings)
    {
        _window ??= new TrackChangeToastWindow();
        _window.ShowToast(title, artist, art, isLightTheme, screen,
            settings.TrackChangeToastPosition, settings.TrackChangeToastSize, settings.TrackChangeToastWidth);
    }

    public void Dispose()
    {
        _window?.Close();
        _window = null;
    }
}

internal static class ToastMonitorResolver
{
    // Конкретный DeviceName приоритетнее автоматического выбора. Если монитор больше не
    // подключён, используем монитор HWND главного окна, а не жёстко основной экран Windows.
    public static System.Windows.Forms.Screen Resolve(AppSettings settings, IntPtr ownerHandle)
    {
        if (!string.IsNullOrEmpty(settings.TrackChangeToastMonitor))
        {
            var saved = System.Windows.Forms.Screen.AllScreens
                .FirstOrDefault(screen => screen.DeviceName == settings.TrackChangeToastMonitor);
            if (saved is not null) return saved;
        }

        if (ownerHandle != IntPtr.Zero) return System.Windows.Forms.Screen.FromHandle(ownerHandle);
        return System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
    }
}
