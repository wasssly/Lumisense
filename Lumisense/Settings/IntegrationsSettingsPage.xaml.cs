using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class IntegrationsSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;

    internal IntegrationsSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        DiscordRichPresenceEnabledCheckBox.IsChecked = _settings.DiscordRichPresenceEnabled;
        DiscordRichPresenceShowTrackInfoCheckBox.IsChecked = _settings.DiscordRichPresenceShowTrackInfo;
        DiscordRichPresenceShowTimelineCheckBox.IsChecked = _settings.DiscordRichPresenceShowTimeline;
        DiscordRichPresenceShowCoverArtCheckBox.IsChecked = _settings.DiscordRichPresenceShowCoverArt;
        DiscordRichPresenceShowButtonsCheckBox.IsChecked = _settings.DiscordRichPresenceShowButtons;
        DiscordRichPresenceShowDownloadButtonCheckBox.IsChecked = _settings.DiscordRichPresenceShowDownloadButton;
        UpdateDiscordRichPresenceConnectionStatus();
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageIntegrations, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

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
            LocalizedMessageBox.Show(_window, $"Не удалось открыть журнал Discord:\n{ex.Message}",
                "Discord Rich Presence", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    public void UpdateDiscordRichPresenceConnectionStatus()
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
