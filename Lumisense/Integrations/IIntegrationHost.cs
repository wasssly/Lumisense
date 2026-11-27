using System.Windows;

namespace Lumisense;

// Единственная граница между MainWindowIntegrationController и MainWindow: список того, что нужно
// трём desktop-интеграциям (media hotkeys, Now Playing/SMTC, трей), и ничего больше. MainWindow
// реализует это явно (void IIntegrationHost.Xxx(...)) — сами методы остаются private и вызвать их
// в обход этого контракта можно только осознанным кастом к IIntegrationHost, а не просто через
// window.Xxx(...) откуда угодно в сборке.
internal interface IIntegrationHost
{
    bool IsPlaying { get; }

    void PlayPauseButton_Click(object sender, RoutedEventArgs e);
    void StopButton_Click(object sender, RoutedEventArgs e);
    void PrevButton_Click(object sender, RoutedEventArgs e);
    void PlayNextTrack(TrackChangeOrigin changeOrigin = TrackChangeOrigin.User);
    void HandleHotkeyNext(int virtualKey);
    void HandleHotkeyPrevious(int virtualKey);
    void ShuffleButton_Click(object sender, RoutedEventArgs e);
    void RepeatButton_Click(object sender, RoutedEventArgs e);
    void DeleteCurrentTrackFromDiskHotkey();
    void SeekBy(double seconds);
    void ChangeVolumeBy(double delta);
    void ToggleMute();
    void ToggleMiniPlayerHotkey();
    void RestoreFromTray();
    void ExitApplicationCompletely();
    void LyricsPanelButton_Click(object sender, RoutedEventArgs e);
}
