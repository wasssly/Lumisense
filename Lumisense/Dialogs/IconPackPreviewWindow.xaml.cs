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

    // Обработчик на Border, а не на внутреннем Grid заголовка — тот начинается ниже верхнего
    // Padding="22" и не покрывал самую верхушку окна. Порог по Y отсекает список иконок ниже.
    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement border) return;

        const double headerZoneHeight = 60;
        if (e.GetPosition(border).Y <= headerZoneHeight)
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
