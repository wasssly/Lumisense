namespace Lumisense;

// Реализация IDiagnosticsHost: явная, чтобы не расширять публичную поверхность главного окна.
public partial class MainWindow : IDiagnosticsHost
{
    bool IDiagnosticsHost.HasTrack => _currentTrackPath != null;

    bool IDiagnosticsHost.IsPlaying => _isPlaying;

    bool IDiagnosticsHost.IsMiniMode => _isMiniMode;

    bool IDiagnosticsHost.IsMainWindowVisible => IsVisible && !_isMiniMode;

    bool IDiagnosticsHost.IsNowPlayingOpen => _nowPlayingWindow is not null;

    void IDiagnosticsHost.SetPlaying(bool playing)
    {
        if (_isPlaying != playing)
            ExternalPlayPause();
    }

    void IDiagnosticsHost.ShowMainWindow()
    {
        if (_isMiniMode)
            ExitMiniMode();
        else if (!IsVisible)
            RestoreFromTray();
    }

    void IDiagnosticsHost.ShowMiniPlayer()
    {
        if (!_isMiniMode)
            EnterMiniMode();
    }

    void IDiagnosticsHost.HideToTray()
    {
        if (_isMiniMode)
            ExitMiniMode();

        Hide();
    }

    void IDiagnosticsHost.SetNowPlayingOpen(bool open)
    {
        if (open)
            ShowNowPlayingWindow();
        else
            _nowPlayingWindow?.Close();
    }

    void IDiagnosticsHost.NextTrack() => ExternalNext();

    IDiagnosticsHost ISettingsDiagnosticsHost.GetDiagnosticsHost() => this;
}
