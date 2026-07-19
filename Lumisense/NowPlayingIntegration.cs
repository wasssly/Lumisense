using System.Windows;
using System.Windows.Interop;
using Windows.Media;

namespace AudioPlayer;

/// <summary>
/// Интеграция с "Now Playing" в Windows 11 (медиа-виджет на панели задач, блокировка экрана,
/// беспроводные наушники с кнопками) через System Media Transport Controls.
/// Требует TargetFramework вида net8.0-windows10.0.xxxxx.0, чтобы WinRT-типы были доступны.
/// </summary>
public sealed class NowPlayingIntegration
{
    private readonly SystemMediaTransportControls _controls;
    private readonly SystemMediaTransportControlsDisplayUpdater _updater;

    public event Action? PlayRequested;
    public event Action? PauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? StopRequested;

    public NowPlayingIntegration(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        // Для классических Win32/WPF-приложений SMTC получают именно через интероп по хендлу окна
        _controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.IsStopEnabled = true;
        _controls.ButtonPressed += OnButtonPressed;

        _updater = _controls.DisplayUpdater;
        _updater.Type = MediaPlaybackType.Music;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: PlayRequested?.Invoke(); break;
            case SystemMediaTransportControlsButton.Pause: PauseRequested?.Invoke(); break;
            case SystemMediaTransportControlsButton.Next: NextRequested?.Invoke(); break;
            case SystemMediaTransportControlsButton.Previous: PreviousRequested?.Invoke(); break;
            case SystemMediaTransportControlsButton.Stop: StopRequested?.Invoke(); break;
        }
    }

    public void UpdateTrackInfo(string title, string artist)
    {
        _updater.MusicProperties.Title = title;
        _updater.MusicProperties.Artist = artist;
        _updater.Update();
    }

    public void SetPlaybackStatus(MediaPlaybackStatus status)
    {
        _controls.PlaybackStatus = status;
    }
}
