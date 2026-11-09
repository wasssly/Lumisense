using System.Windows;
using System.Windows.Media;

namespace Lumisense;

internal readonly record struct DroppedAddResult(int Added, bool FoundSupported);

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
    double CurrentVolume { get; }
    string CurrentTitle { get; }
    string CurrentArtist { get; }
    Brush? CurrentArtBrush { get; }
    string? CurrentTrackPath { get; }
    bool CurrentIsShuffleEnabled { get; }
    string CurrentRepeatModeName { get; }
    bool EffectiveGameOverlayCompatibilityEnabled { get; }
    // +1, если трек сменили вперёд, -1 — назад (кнопки, остров, горячие клавиши): по нему обложка выезжает с нужной стороны.
    int LastTrackChangeDirection { get; }
    // Живое положение и длительность: снимок состояния на паузе не обновляется, и остров показывал бы старое время.
    double CurrentPlaybackSeconds { get; }
    double CurrentTrackDurationSeconds { get; }
    AudioLevelSampleProvider? AudioLevelMeter { get; }
    // Устройство вывода звука: имя и тип, плюс событие при смене (Announce — стоит показать пользователю).
    event Action<AudioOutputDeviceInfo>? OutputDeviceChanged;
    string CurrentOutputDeviceName { get; }
    AudioOutputKind CurrentOutputKind { get; }

    // Остров просит считать уровень и спектр, пока у него идут эквалайзер или фон; Now Playing просит отдельно, измеритель считает, пока нужен хоть одному.
    void SetIslandAudioLevelWanted(bool wanted);
    Color GetResolvedAccentColor();
    Color GetProgressBarColor(ProgressSurface surface);

    void ExternalPlayPause();
    void ExternalNext();
    void ExternalPrev();
    void ExternalToggleShuffle();
    void ExternalToggleRepeat();
    void ExternalToggleFavoriteCurrentTrack();
    void ExternalSeekRatio(double ratio);
    void ExternalChangeVolume(double delta);

    Task<DroppedAddResult> AddDroppedPathsAsync(IReadOnlyList<string> paths);

    void ExitMiniMode(bool returnToMiniOnNextTaskbarMinimize = false);
    void ShowSettingsWindow(string? section = null);
    void ShowNowPlayingWindow();
    void ShowCoverPreview(Window owner);

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
