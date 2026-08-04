using System.Windows;
using Wpf.Ui.Controls;

namespace AudioPlayer;

public enum ProfileTransferMode
{
    Export,
    Import
}

// Результат — какие секции пользователь выбрал перенести. Сами объекты секций сюда не
// попадают: этот диалог только спрашивает "что", а забирает/применяет данные вызывающий код
// (SettingsWindow.ExportProfileButton_Click / ImportProfileButton_Click) — диалогу не нужно
// знать ни про AppSettings, ни про плейлист, ни про формат .lumi-файла.
public sealed class ProfileTransferSelection
{
    public bool IncludeSettings { get; init; }
    public bool IncludePlaylist { get; init; }
    public bool IncludeFavorites { get; init; }
}

public partial class ProfileTransferWindow : FluentWindow
{
    // заполнено только если ShowDialog() вернул true
    public ProfileTransferSelection Result { get; private set; } = new();

    // availableSections — только для Mode.Import: какие секции реально есть в открытом
    // .lumi-файле (см. LumiProfile — null-секция значит "её не было при экспорте"). Чекбокс
    // секции, которой в файле нет, выключается и снимается — предлагать импортировать то,
    // чего физически нет в файле, бессмысленно. Для Mode.Export параметр не передаётся —
    // экспортировать можно любую комбинацию из того, что есть в приложении прямо сейчас.
    public ProfileTransferWindow(ProfileTransferMode mode, ProfileTransferSelection? availableSections = null)
    {
        InitializeComponent();

        if (mode == ProfileTransferMode.Export)
        {
            HeaderText.Text = "Что экспортировать?";
            SubHeaderText.Text = "Выбранное сохранится в один .lumi-файл, который можно перенести на другой компьютер.";
            ConfirmButton.Content = "Экспортировать";
        }
        else
        {
            HeaderText.Text = "Что импортировать?";
            SubHeaderText.Text = "Плейлист и избранное добавятся к уже открытым, не заменяя их. Часть настроек может потребовать перезапуска плеера.";
            ConfirmButton.Content = "Импортировать";

            if (availableSections != null)
            {
                SettingsCheckBox.IsEnabled = availableSections.IncludeSettings;
                SettingsCheckBox.IsChecked = availableSections.IncludeSettings;

                PlaylistCheckBox.IsEnabled = availableSections.IncludePlaylist;
                PlaylistCheckBox.IsChecked = availableSections.IncludePlaylist;

                FavoritesCheckBox.IsEnabled = availableSections.IncludeFavorites;
                FavoritesCheckBox.IsChecked = availableSections.IncludeFavorites;
            }
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new ProfileTransferSelection
        {
            IncludeSettings = SettingsCheckBox.IsChecked == true,
            IncludePlaylist = PlaylistCheckBox.IsChecked == true,
            IncludeFavorites = FavoritesCheckBox.IsChecked == true
        };

        if (!Result.IncludeSettings && !Result.IncludePlaylist && !Result.IncludeFavorites)
        {
            System.Windows.MessageBox.Show(this, "Выберите хотя бы один пункт.", "Ничего не выбрано",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
