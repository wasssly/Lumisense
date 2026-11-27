namespace Lumisense;

// Что встроенной диагностике нужно от главного окна: состояние плеера и переключение режимов окна для сценарных замеров.
internal interface IDiagnosticsHost
{
    bool HasTrack { get; }

    bool IsPlaying { get; }

    bool IsMiniMode { get; }

    bool IsMainWindowVisible { get; }

    bool IsNowPlayingOpen { get; }

    void SetPlaying(bool playing);

    void ShowMainWindow();

    void ShowMiniPlayer();

    void HideToTray();

    void SetNowPlayingOpen(bool open);

    void NextTrack();
}
