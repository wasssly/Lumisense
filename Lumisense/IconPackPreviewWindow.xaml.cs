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

    // Раньше висел на внутреннем Grid строки заголовка — тот начинается только НИЖЕ верхнего
    // Padding="22" внешнего Border, поэтому у самой верхушки окна (в этом отступе) перетаскивание
    // не срабатывало, а чуть ниже, на уровне текста заголовка — работало. Теперь обработчик на
    // самом Border, а порог по Y отсекает область со списком иконок ниже, чтобы клик по пустому
    // месту между плитками не пытался таскать окно.
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
