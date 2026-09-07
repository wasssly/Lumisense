using System.Windows;
using System.Windows.Input;

namespace Lumisense;

public partial class IconPackPreviewWindow : Window
{
    public IconPackPreviewWindow(Window owner, string pack)
    {
        InitializeComponent();
        Owner = owner;
        Tag = pack;
        TitleText.Text = $"Все иконки — {pack}";
        IconsItemsControl.ItemsSource = IconPacks.IconNames;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
