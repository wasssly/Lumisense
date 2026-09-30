using System.Windows.Media;

namespace Lumisense;

// Всё, что MiniPlayerWindow берёт у MainWindow: окно зависит от этого интерфейса, а не от всего MainWindow.
internal interface IMiniPlayerHost
{
    AppSettings Settings { get; }
    PlaybackStateStore PlaybackState { get; }
    event Action<string, string, Brush?>? TrackInfoChanged;
    event Action<double>? VolumeChanged;
    event Action<string>? RepeatModeChanged;
    event Action<bool>? ShuffleStateChanged;

    bool IsPlayingNow { get; }
    string CurrentTitle { get; }
    string CurrentArtist { get; }
    Brush? CurrentArtBrush { get; }
    string? CurrentTrackPath { get; }
    bool CurrentIsShuffleEnabled { get; }
    string CurrentRepeatModeName { get; }
    bool EffectiveGameOverlayCompatibilityEnabled { get; }
    Color GetResolvedAccentColor();

    void ExternalPlayPause();
    void ExternalNext();
    void ExternalPrev();
    void ExternalToggleShuffle();
    void ExternalToggleRepeat();
    void ExternalToggleFavoriteCurrentTrack();
    void ExternalSeekRatio(double ratio);
    void ExternalChangeVolume(double delta);

    void ExitMiniMode(bool returnToMiniOnNextTaskbarMinimize = false);
    void ShowSettingsWindow(string? section = null);
    void ShowNowPlayingWindow();

    void ApplyMiniPlayerProgressBarVisibilityLive();
    void ApplyMiniPlayerArtworkProgressVisibilityLive();
    void ApplyMiniPlayerArtworkStyleLive();
    void ApplyMiniPlayerButtonsLayoutLive();
    void ApplyMiniPlayerOverlayCompatibilityLive(bool enabled);
    void SetMiniPlayerSecondaryButtonMode(string? mode);
    void SetPlaybackRateFromMiniPlayer(double speed);
    void SetPlaybackPitchFromMiniPlayer(double semitones);
    void SetMiniPlayerPinned(bool pinned);
    void SetMiniPlayerTopmost(bool topmost);
    void SetMiniPlayerOpacity(double opacity);
    void SaveMiniPlayerPosition(double left, double top);
}
