using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

public partial class SettingsWindow
{
    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        // Показанные результаты поиска хранят уже выведенные подписи: пересобираем их на новом языке.
        if (SettingsSearchBox.Text.Trim().Length > 0) RefreshSearchResults();

        if (_lazyPages.TryGetValue("About", out ILazySettingsPage? aboutPage)) ((AboutSettingsPage)aboutPage).RefreshAppVersionText();
        if (_lazyPages.TryGetValue("Playback", out ILazySettingsPage? playbackPage)) ((PlaybackSettingsPage)playbackPage).RefreshLocalization();
        if (_lazyPages.TryGetValue("Notifications", out ILazySettingsPage? toastPage)) ((NotificationsSettingsPage)toastPage).RefreshMonitorCombo();
        RefreshIslandMonitorCombo();
        if (_lazyPages.TryGetValue("Integrations", out ILazySettingsPage? discordPage)) ((IntegrationsSettingsPage)discordPage).UpdateDiscordRichPresenceConnectionStatus();

        if (_lazyPages.TryGetValue("Equalizer", out ILazySettingsPage? eqPage)) ((EqualizerSettingsPage)eqPage).RefreshBandTexts();

        if (_lazyPages.TryGetValue("Updates", out ILazySettingsPage? updatesPage)) ((UpdatesSettingsPage)updatesPage).RefreshLocalization();

        if (_lazyPages.TryGetValue("Lyrics", out ILazySettingsPage? lyricsPage)) ((LyricsSettingsPage)lyricsPage).RefreshLocalization();
        RefreshMiniArtworkProgressThicknessPresentation();
    }
}
