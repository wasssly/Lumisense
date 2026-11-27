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
    private void RememberVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.RememberVolume = RememberVolumeCheckBox.IsChecked == true;
    }

    private void LogarithmicVolumeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.UseLogarithmicVolume = LogarithmicVolumeCheckBox.IsChecked == true;
        _owner.RefreshVolumeCurve();
    }

    private void NeverAutoPlayLastTrackOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.NeverAutoPlayLastTrackOnStartup = NeverAutoPlayLastTrackOnStartupCheckBox.IsChecked == true;
    }

    private void TrackLoadTraceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.TrackLoadTraceEnabled = TrackLoadTraceCheckBox.IsChecked == true;
        AudioDiagnosticsCopyStatusText.Text = _settings.TrackLoadTraceEnabled
            ? LocalizationService.Translate("Trace подготовки трека включён")
            : LocalizationService.Translate("Trace подготовки трека выключен");
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        // Показанные результаты поиска хранят уже выведенные подписи: пересобираем их на новом языке.
        if (SettingsSearchBox.Text.Trim().Length > 0) RefreshSearchResults();

        if (_lazyPages.TryGetValue("About", out ILazySettingsPage? aboutPage)) ((AboutSettingsPage)aboutPage).RefreshAppVersionText();
        InitializeOutputDeviceCombo();
        if (_lazyPages.TryGetValue("Notifications", out ILazySettingsPage? toastPage)) ((NotificationsSettingsPage)toastPage).RefreshMonitorCombo();
        InitializeIslandMonitorCombo();
        if (_lazyPages.TryGetValue("Integrations", out ILazySettingsPage? discordPage)) ((IntegrationsSettingsPage)discordPage).UpdateDiscordRichPresenceConnectionStatus();

        if (_lazyPages.TryGetValue("Equalizer", out ILazySettingsPage? eqPage)) ((EqualizerSettingsPage)eqPage).RefreshBandTexts();

        if (_lazyPages.TryGetValue("Updates", out ILazySettingsPage? updatesPage)) ((UpdatesSettingsPage)updatesPage).RefreshLocalization();

        if (_lazyPages.TryGetValue("Lyrics", out ILazySettingsPage? lyricsPage)) ((LyricsSettingsPage)lyricsPage).RefreshLocalization();
        RefreshMiniArtworkProgressThicknessPresentation();
    }
}
