using System.Windows.Media;

namespace Lumisense;

// Координирует переиспользуемое toast-окно и выбор экрана. Политика «показывать ли сейчас»
// остаётся в MainWindow, потому что зависит от причины смены трека и состояния playback.
internal sealed class TrackChangeToastController : IDisposable
{
    private TrackChangeToastWindow? _window;

    public void Show(string title, string artist, Brush? art, bool isLightTheme,
        System.Windows.Forms.Screen screen, AppSettings settings, Color? tint = null)
    {
        // Пока пользователь выбирает место, смена трека не должна подменять карточку и прятать её.
        if (_window?.IsPickingPosition == true) return;

        _window ??= new TrackChangeToastWindow();
        _window.ApplyOverlayCompatibilityLive(settings.GameOverlayCompatibilityMode);
        _window.ShowToast(title, artist, art, isLightTheme, screen,
            settings.TrackChangeToastPosition, settings.TrackChangeToastSize, settings.TrackChangeToastWidth,
            settings.TrackChangeToastArtSide, settings.TrackChangeToastTextAlignment,
            settings.TrackChangeToastArtNextToText, settings.TrackChangeToastOpacity,
            settings.TrackChangeToastCustomX, settings.TrackChangeToastCustomY,
            tint, settings.TrackChangeToastColorStrength);
    }

    public void PickPosition(string title, string hint, Brush? art, bool isLightTheme,
        System.Windows.Forms.Screen screen, AppSettings settings, Color? tint, Action<ToastPositionPickResult?> completed)
    {
        _window ??= new TrackChangeToastWindow();
        _window.ApplyOverlayCompatibilityLive(settings.GameOverlayCompatibilityMode);
        _window.BeginPositionPick(title, hint, art, isLightTheme, screen, settings, tint, completed);
    }

    public void ApplyOverlayCompatibilityLive(bool enabled)
    {
        _window?.ApplyOverlayCompatibilityLive(enabled);
    }

    public void Dispose()
    {
        _window?.Close();
        _window = null;
    }
}

internal static class ToastMonitorResolver
{
    // Конкретный DeviceName приоритетнее автоматического выбора. Если монитор не выбран или больше не подключён,
    // берётся монитор окна плеера (playerScreen), а при его отсутствии — основной экран Windows.
    public static System.Windows.Forms.Screen Resolve(AppSettings settings, Func<System.Windows.Forms.Screen?> playerScreen)
    {
        if (!string.IsNullOrEmpty(settings.TrackChangeToastMonitor))
        {
            var saved = System.Windows.Forms.Screen.AllScreens
                .FirstOrDefault(screen => screen.DeviceName == settings.TrackChangeToastMonitor);
            if (saved is not null) return saved;
        }

        return playerScreen() ?? System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
    }

    // Свёрнутое окно Windows «паркует» в (-32000; -32000), а спрятанное в трей сохраняет прежнее место только в RestoreBounds:
    // по HWND такого окна монитор не определить, он всегда оказывался основным, и уведомление «сбрасывалось» туда при смене трека.
    internal static bool IsParkedPosition(double left, double top) => left <= -30000 && top <= -30000;
}
