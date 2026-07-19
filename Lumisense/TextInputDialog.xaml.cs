using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace AudioPlayer;

/// <summary>
/// Небольшое модальное окно "введите текст" — переиспользуемо для любого места, где нужно
/// спросить у пользователя короткую строку (сейчас используется только для названия новой
/// ручной папки плейлиста — см. MainWindow.CreateFolderMenuItem_Click).
///
/// Использование:
///     var dialog = new TextInputDialog("Новая папка", "Название папки:", "Моя папка") { Owner = this };
///     if (dialog.ShowDialog() == true)
///         MessageBox.Show(dialog.ResultText);
/// </summary>
public partial class TextInputDialog : FluentWindow
{
    /// <summary>Введённый пользователем текст (уже обрезан от пробелов по краям).
    /// Заполнен только когда ShowDialog() вернул true.</summary>
    public string ResultText { get; private set; } = "";

    public TextInputDialog(string title, string prompt, string defaultText = "")
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultText;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryAccept();
        else if (e.Key == Key.Escape) CancelButton_Click(sender, e);
    }

    private void TryAccept()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            InputBox.Focus();
            return;
        }

        ResultText = text;
        DialogResult = true;
        Close();
    }
}
