using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumisense;

// Всё, что NowPlayingWindow берёт у MainWindow: окно зависит от этого интерфейса, а не от всего MainWindow.
internal interface INowPlayingHost
{
    AppSettings Settings { get; }
    PlaybackStateStore PlaybackState { get; }
    event Action<string, string, Brush?>? TrackInfoChanged;

    bool IsPlayingNow { get; }
    string CurrentTitle { get; }
    string CurrentArtist { get; }
    BitmapImage? CurrentAlbumArt { get; }
    string? CurrentTrackPath { get; }
    double CurrentPlaybackSeconds { get; }
    double CurrentTrackDurationSeconds { get; }
    AudioLevelSampleProvider? AudioLevelMeter { get; }

    void ExternalSeekRatio(double ratio);
    void ExternalPlayPause();
    void ExternalNext();
    void ExternalPrev();
}
