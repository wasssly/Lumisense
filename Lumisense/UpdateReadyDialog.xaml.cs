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

    private void DialogBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Клики по кнопкам уже помечаются ими как обработанные и сюда не доходят. Клик по
        // остальной области диалога равносилен «Позже» — не хотим, чтобы случайный клик мимо
        // кнопок трактовался как согласие на немедленный рестарт.
        if (e.OriginalSource is not Button)
        {
            e.Handled = true;
            LaterButton_Click(sender, e);
        }
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
