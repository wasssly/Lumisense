using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace AudioPlayer;

// Модальный диалог "введите текст" — сейчас нужен только для названия новой ручной папки
// плейлиста (MainWindow.CreateFolderMenuItem_Click)
public partial class TextInputDialog : FluentWindow
{
    // заполнено только если ShowDialog() вернул true
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
