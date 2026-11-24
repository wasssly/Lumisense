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
    private void DiscordRichPresenceEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DiscordRichPresenceEnabled = DiscordRichPresenceEnabledCheckBox.IsChecked == true;
        UpdateDiscordRichPresenceConnectionStatus();
        _owner.ApplyDiscordRichPresenceSettingsLive();
    }

    private void ConnectDiscordButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.DiscordRichPresenceEnabled = true;
        DiscordRichPresenceEnabledCheckBox.IsChecked = true;
        DiscordRichPresenceLogger.Info("Пользователь включил Discord Rich Presence из настроек.");
        UpdateDiscordRichPresenceConnectionStatus();
        _owner.ApplyDiscordRichPresenceSettingsLive();
    }

    private void OpenDiscordDiagnosticsLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DiscordRichPresenceLogger.LogFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(this, $"Не удалось открыть журнал Discord:\n{ex.Message}",
                "Discord Rich Presence", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void UpdateDiscordRichPresenceConnectionStatus()
    {
        ConnectDiscordButton.Content = LocalizationService.Translate(_settings.DiscordRichPresenceEnabled
            ? "Обновить подключение Discord"
            : "Подключить Discord");
        DiscordRichPresenceConnectionStatusText.Text = LocalizationService.Translate(_settings.DiscordRichPresenceEnabled
            ? "Discord Rich Presence включён. При начале воспроизведения Lumisense обновит ваш статус."
            : "Нажмите «Подключить Discord», чтобы включить Rich Presence с официальным приложением Lumisense.");
    }

    private void DiscordRichPresencePrivacyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.DiscordRichPresenceShowTrackInfo = DiscordRichPresenceShowTrackInfoCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowTimeline = DiscordRichPresenceShowTimelineCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowCoverArt = DiscordRichPresenceShowCoverArtCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowButtons = DiscordRichPresenceShowButtonsCheckBox.IsChecked == true;
        _settings.DiscordRichPresenceShowDownloadButton = DiscordRichPresenceShowDownloadButtonCheckBox.IsChecked == true;
        _owner.ApplyDiscordRichPresenceSettingsLive();
    }
}
