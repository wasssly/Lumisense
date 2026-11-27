using System.Windows;
using System.Windows.Input;

namespace Lumisense;

// Предупреждение при переходе на зеркало загрузки обновлений; DialogResult == true значит «использовать зеркало».
public partial class UpdateMirrorWarningDialog : Window
{
    public UpdateMirrorWarningDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;

        // Текст задаётся здесь, чтобы он проходил через словарь перевода (XAML-строки окна не переводятся).
        TitleText.Text = LocalizationService.Translate("Скачивание через зеркало");
        MessageText.Text = LocalizationService.Translate("Зеркало увидит ваш IP-адрес и факт скачивания обновления, но не может подменить файл: SHA-256 установщика сверяется с данными GitHub до запуска. Вернуться на GitHub можно в любой момент в этом же разделе.");
        DontAskAgainCheckBox.Content = LocalizationService.Translate("Больше не спрашивать");
        StayButton.Content = LocalizationService.Translate("Остаться на GitHub");
        UseMirrorButton.Content = LocalizationService.Translate("Использовать зеркало");
    }

    public bool DontAskAgain => DontAskAgainCheckBox.IsChecked == true;

    private void StayButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UseMirrorButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}
