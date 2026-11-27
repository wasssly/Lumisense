using System;

namespace Lumisense;

public partial class SettingsWindow
{
    // Профиль нового устройства изменил эквалайзер и баланс: обновляем открытые элементы.
    public void RefreshSoundProfileControls()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_lazyPages.TryGetValue("Equalizer", out ILazySettingsPage? page)) ((EqualizerSettingsPage)page).RefreshFromOwner();
            if (_lazyPages.TryGetValue("Playback", out ILazySettingsPage? playbackPage)) ((PlaybackSettingsPage)playbackPage).RefreshPlaybackBalance();
        });
    }
}
