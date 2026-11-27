using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Lumisense;

public partial class ScaleRestartDialog : Window
{
    private readonly AppSettings _settings;

    public ScaleRestartDialog(Window owner, AppSettings settings)
    {
        InitializeComponent();
        Owner = owner;
        _settings = settings;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void DialogBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Клики по кнопкам уже помечаются ими как обработанные и сюда не доходят.
        // Для остальной области уведомление закрывается сразу, как «Позже».
        if (e.OriginalSource is not System.Windows.Controls.Button)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        // Фиксируем выбранный масштаб до запуска новой копии, чтобы после перезапуска
        // приложение сразу использовало актуальное значение.
        SettingsManager.Save(_settings);

        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            DialogResult = false;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(processPath)
            {
                UseShellExecute = true
            });
            Application.Current.Shutdown();
        }
        catch
        {
            // Если новая копия не запустилась, оставляем текущее приложение рабочим и закрываем
            // только диалог. Настройка уже сохранена и будет применена при следующем запуске.
            DialogResult = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}

