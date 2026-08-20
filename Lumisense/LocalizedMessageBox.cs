using System.Windows;

namespace AudioPlayer;

// Все системные диалоги проходят через один слой: MessageBox не входит в visual tree,
// поэтому обычный обход окна не может перевести его текст при открытии.
public static class LocalizedMessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        MessageBox.Show(LocalizationService.Translate(messageBoxText));

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        MessageBox.Show(LocalizationService.Translate(messageBoxText), LocalizationService.Translate(caption));

    public static MessageBoxResult Show(string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage icon) =>
        MessageBox.Show(LocalizationService.Translate(messageBoxText), LocalizationService.Translate(caption), button, icon);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption) =>
        MessageBox.Show(owner, LocalizationService.Translate(messageBoxText), LocalizationService.Translate(caption));

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage icon) =>
        MessageBox.Show(owner, LocalizationService.Translate(messageBoxText), LocalizationService.Translate(caption), button, icon);

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption,
        MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        MessageBox.Show(owner, LocalizationService.Translate(messageBoxText), LocalizationService.Translate(caption),
            button, icon, defaultResult);
}
