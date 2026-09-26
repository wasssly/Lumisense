using System.Windows;

namespace Lumisense;

// Владеет тремя desktop-интеграциями, которые MainWindow может не получить (RegisterHotKey занят,
// SMTC недоступен, трей не создался): конструирует их, подписывает на события и оборачивает
// подписчиков в try/catch, чтобы отказ одной интеграции не ронял окно до первого показа.
//
// Работает с MainWindow только через IIntegrationHost — это единственный контракт, который нужен
// wiring-коду (PlayPauseButton_Click, ChangeVolumeBy, SeekBy и т.д.), и MainWindow реализует его
// явно, так что случайно вызвать эти методы напрямую через window.Xxx(...) откуда-то ещё нельзя.
internal sealed class MainWindowIntegrationController
{
    public GlobalMediaHotKeys? HotKeys { get; private set; }
    public NowPlayingIntegration? NowPlaying { get; private set; }
    public TrayIconManager? Tray { get; private set; }

    // Глобальные медиаклавиши работают без фокуса; в try/catch, потому что RegisterHotKey может отказать, если хоткей занят
    // другим приложением, и необработанное исключение роняло плеер до первого показа.
    public void InitializeMediaHotKeys(MainWindow window)
    {
        IIntegrationHost host = window;
        try
        {
            HotKeys = new GlobalMediaHotKeys(window);
            HotKeys.PlayPausePressed += () => window.Dispatcher.BeginInvoke(() => host.PlayPauseButton_Click(window, new RoutedEventArgs()));
            HotKeys.NextPressed += virtualKey => window.Dispatcher.BeginInvoke(() => host.HandleHotkeyNext(virtualKey));
            HotKeys.PreviousPressed += virtualKey => window.Dispatcher.BeginInvoke(() => host.HandleHotkeyPrevious(virtualKey));
            HotKeys.StopPressed += () => window.Dispatcher.BeginInvoke(() => host.StopButton_Click(window, new RoutedEventArgs()));
            HotKeys.VolumeUpPressed += () => window.Dispatcher.BeginInvoke(() => host.ChangeVolumeBy(0.02));
            HotKeys.VolumeDownPressed += () => window.Dispatcher.BeginInvoke(() => host.ChangeVolumeBy(-0.02));
            HotKeys.MutePressed += () => window.Dispatcher.BeginInvoke(host.ToggleMute);
            HotKeys.ShufflePressed += () => window.Dispatcher.BeginInvoke(() => host.ShuffleButton_Click(window, new RoutedEventArgs()));
            HotKeys.RepeatPressed += () => window.Dispatcher.BeginInvoke(() => host.RepeatButton_Click(window, new RoutedEventArgs()));
            HotKeys.DeleteTrackPressed += () => window.Dispatcher.BeginInvoke(host.DeleteCurrentTrackFromDiskHotkey);
            HotKeys.SeekForwardPressed += () => window.Dispatcher.BeginInvoke(() => host.SeekBy(5));
            HotKeys.SeekBackwardPressed += () => window.Dispatcher.BeginInvoke(() => host.SeekBy(-5));
            HotKeys.ToggleFavoritePressed += () => window.Dispatcher.BeginInvoke(window.ExternalToggleFavoriteCurrentTrack);
            HotKeys.ToggleLyricsPressed += () => window.Dispatcher.BeginInvoke(() => host.LyricsPanelButton_Click(window, new RoutedEventArgs()));
            HotKeys.ToggleMiniPlayerPressed += () => window.Dispatcher.BeginInvoke(host.ToggleMiniPlayerHotkey);
            HotKeys.ApplyCustomHotkeys(window.Settings);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось зарегистрировать глобальные горячие клавиши — возможно, какая-то из комбинаций уже занята другим приложением", ex);
            HotKeys = null;
        }
    }

    // Интеграция с Now Playing Windows 11 (панель задач, блокировка экрана, наушники с кнопками)
    public void InitializeNowPlayingIntegration(MainWindow window)
    {
        IIntegrationHost host = window;
        try
        {
            NowPlaying = new NowPlayingIntegration(window);
            NowPlaying.PlayRequested += () => window.Dispatcher.BeginInvoke(() =>
            {
                if (!host.IsPlaying) host.PlayPauseButton_Click(window, new RoutedEventArgs());
            });
            NowPlaying.PauseRequested += () => window.Dispatcher.BeginInvoke(() =>
            {
                if (host.IsPlaying) host.PlayPauseButton_Click(window, new RoutedEventArgs());
            });
            NowPlaying.NextRequested += () => window.Dispatcher.BeginInvoke(() => host.PlayNextTrack());
            NowPlaying.PreviousRequested += () => window.Dispatcher.BeginInvoke(() => host.PrevButton_Click(window, new RoutedEventArgs()));
            NowPlaying.StopRequested += () => window.Dispatcher.BeginInvoke(() => host.StopButton_Click(window, new RoutedEventArgs()));
        }
        catch (Exception ex)
        {
            // SMTC недоступен в некоторых окружениях (например, без нужного Windows SDK
            // на машине сборки) — в этом случае просто отключаем интеграцию, плеер работает дальше
            Logger.Error("Не удалось включить интеграцию с Now Playing (SMTC)", ex);
            NowPlaying = null;
        }
    }

    // Трей тоже в try/catch: иконка не критична, а необработанное исключение уронило бы окно до первого показа.
    public void InitializeTrayIcon(MainWindow window)
    {
        IIntegrationHost host = window;
        try
        {
            Tray = new TrayIconManager(window);
            Tray.OpenRequested += host.RestoreFromTray;
            Tray.SettingsRequested += () => window.Dispatcher.BeginInvoke(() => window.ShowSettingsWindow());
            Tray.ExitRequested += host.ExitApplicationCompletely;
            Tray.PlayPauseRequested += () => window.Dispatcher.BeginInvoke(() => host.PlayPauseButton_Click(window, new RoutedEventArgs()));
            Tray.NextRequested += () => window.Dispatcher.BeginInvoke(() => host.PlayNextTrack());
            Tray.PreviousRequested += () => window.Dispatcher.BeginInvoke(() => host.PrevButton_Click(window, new RoutedEventArgs()));
            window.PlaybackStateChanged += isPlaying => Tray?.SetPlayingState(isPlaying);
            window.TrackInfoChanged += (title, artist, _) => Tray?.SetNowPlaying(title, artist, window.CurrentAlbumArtBytes);
            Tray.SetPlayingState(host.IsPlaying);
            Tray.ApplyTheme(isLight: window.Settings.IsLightThemeResolved());
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось создать значок в трее", ex);
            Tray = null;
        }
    }
}
