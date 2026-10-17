using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lumisense;

// Показывается после скачивания Velopack-обновления — вместо немедленного автоматического
// рестарта даёт выбор: сейчас или позже (см. UpdateAvailableWindow._velopackReadyToApply).
public partial class UpdateReadyDialog : Window
{
    public bool RestartNow { get; private set; }

    public UpdateReadyDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;

        TitleText.Text = LocalizationService.Translate("Обновление готово");
        MessageText.Text = LocalizationService.Translate(
            "Новая версия Lumisense загружена и готова к установке. Перезапустить сейчас, чтобы применить её, или продолжить работу и сделать это позже?");
        LaterButton.Content = LocalizationService.Translate("Позже");
        RestartButton.Content = LocalizationService.Translate("Перезапустить сейчас");
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        RestartNow = false;
        DialogResult = false;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        RestartNow = true;
        DialogResult = true;
    }

    // Клик по свободному месту диалога ничего не закрывает: раньше он равнялся «Позже», и окно пропадало от случайного клика.
    // Закрыть диалог можно только кнопками или Esc. Окно без рамки, поэтому за свободное место его можно перетащить.
    private void DialogBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button || e.OriginalSource is DependencyObject source && FindParentButton(source) is not null)
            return;

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            e.Handled = true;
            DragMove();
        }
    }

    private static Button? FindParentButton(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is Button button) return button;
        }

        return null;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            LaterButton_Click(sender, e);
        }
    }
}
