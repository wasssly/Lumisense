using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Lumisense;

// Обращение к пользователям: показывается при каждом запуске, пока не отмечено «Не показывать» (AppSettings.CommunityDialogDismissed).
public partial class CommunityDialog : Window
{
    public bool ShouldStopShowing => DontShowCheckBox.IsChecked == true;

    public CommunityDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
    }

    private void TelegramButton_Click(object sender, RoutedEventArgs e) => OpenLinkAndClose(UpdateCheckHealth.TelegramChannelUrl);

    private void GitHubButton_Click(object sender, RoutedEventArgs e) => OpenLinkAndClose(DiscordRichPresenceDefaults.GitHubRepositoryUrl);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = false;
    }

    private void OpenLinkAndClose(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Нет браузера по умолчанию — не критично, просто ничего не открылось.
        }

        DialogResult = true;
    }
}
