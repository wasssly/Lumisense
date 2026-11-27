using System;
using System.Windows;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class ProfileSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    internal ProfileSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();
        RefreshResetRecoveryButton();
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageProfile, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = LocalizationService.Translate(LumiProfileIO.FileFilter),
            DefaultExt = LumiProfileIO.FileExtension,
            FileName = "Lumisense" + LumiProfileIO.FileExtension
        };
        if (saveDialog.ShowDialog(_window) != true) return;

        try
        {
            LumiProfileIO.Export(saveDialog.FileName, _settings);

            LocalizedMessageBox.Show(_window, "Настройки сохранены.", "Экспорт завершён",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(_window, $"Не удалось сохранить файл:\n{ex.Message}", "Ошибка экспорта",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = LocalizationService.Translate(LumiProfileIO.FileFilter) };
        if (openDialog.ShowDialog(_window) != true) return;

        var profile = LumiProfileIO.TryReadFile(openDialog.FileName);
        if (profile == null)
        {
            LocalizedMessageBox.Show(_window, "Не удалось прочитать этот файл — он повреждён или это не .lumi-профиль.",
                "Ошибка импорта", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        // Как у сброса: подтверждение и точка восстановления, чтобы неудачный (чужой или старый) профиль можно было откатить.
        var confirm = LocalizedMessageBox.Show(_window, LocalizationService.Get(LocalizationKey.ProfileImportConfirm),
            LocalizationService.Get(LocalizationKey.ProfileImportConfirmTitle),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!SettingsResetRecoveryService.TryCreateSnapshot(_settings))
        {
            LocalizedMessageBox.Show(_window, LocalizationService.Get(LocalizationKey.ProfileImportSnapshotCreateFailed),
                LocalizationService.Get(LocalizationKey.ProfileResetCancelledTitle),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        LumiProfileIO.Apply(profile.Settings, _settings);
        LocalizationService.ChangeLanguage(_settings, _settings.Language);
        _owner.ApplyImportedSettingsLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");

        LocalizedMessageBox.Show(_window,
            "Настройки импортированы.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера) применится полностью после перезапуска плеера.",
            "Импорт завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

        // Поля окна читаются из _settings один раз в конструкторе и после импорта не переприменяются — проще переоткрыть
        // окно (MainWindow.ShowSettingsWindow), чем обновлять каждое поле формы.
        _window.Close();
        _owner.ShowSettingsWindow("Profile");
    }

    // Необратимый массовый сброс (AppSettings/LumiProfileIO.ResetToDefaults, ResetPlayerButton), устроенный как импорт
    // профиля: подтверждение, сброс, сохранение, живое применение части, предупреждение о перезапуске, переоткрытие окна.
    private void ResetPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(_window,
            "Сбросить тему, акцент, подложку окна, вид и размер плеера, громкость, шафл/повтор, горячие клавиши, мини-плеер и остальные настройки к значениям по умолчанию?\n\n" +
            "Плейлист, избранное, история прослушиваний, статистика и сохранённые пресеты эквалайзера затронуты не будут.",
            "Сбросить плеер?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!SettingsResetRecoveryService.TryCreateSnapshot(_settings))
        {
            LocalizedMessageBox.Show(_window, LocalizationService.Get(LocalizationKey.ProfileResetSnapshotCreateFailedSettings),
                LocalizationService.Get(LocalizationKey.ProfileResetCancelledTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        LumiProfileIO.ResetToDefaults(_settings);
        LocalizationService.ChangeLanguage(_settings, _settings.Language);
        _owner.ApplyImportedSettingsLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");

        LocalizedMessageBox.Show(_window,
            "Плеер сброшен к исходным настройкам.\n\nЧасть из них (хоткеи, эквалайзер, поведение трея и мини-плеера, размер и положение окна) применится полностью после перезапуска плеера.",
            "Сброс завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

        _window.Close();
        _owner.ShowSettingsWindow("Profile");
    }

    private void RestoreResetSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(_window,
            LocalizationService.Get(LocalizationKey.ProfileRestoreConfirm),
            LocalizationService.Get(LocalizationKey.ProfileRestoreConfirmTitle), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!_owner.TryRestoreLastSettingsReset())
        {
            LocalizedMessageBox.Show(_window, LocalizationService.Get(LocalizationKey.ProfileRestoreUnavailable),
                LocalizationService.Get(LocalizationKey.ProfileRestoreUnavailableTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            RefreshResetRecoveryButton();
            return;
        }

        LocalizationService.ChangeLanguage(_settings, _settings.Language);
        LocalizedMessageBox.Show(_window,
            LocalizationService.Get(LocalizationKey.ProfileRestoreCompleted),
            LocalizationService.Get(LocalizationKey.ProfileRestoreCompletedTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        _window.Close();
        _owner.ShowSettingsWindow("Profile");
    }

    private void ResetAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = LocalizedMessageBox.Show(_window,
            "Будут удалены настройки, сохранённые плейлисты, избранное, история прослушиваний, статистика и пресеты эквалайзера.\n\n" +
            "Аудиофайлы на диске не удаляются. Продолжить?",
            "Полный сброс данных", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var secondConfirm = LocalizedMessageBox.Show(_window,
            LocalizationService.Get(LocalizationKey.ProfileResetFullConfirm),
            "Подтвердите полный сброс", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (secondConfirm != System.Windows.MessageBoxResult.Yes) return;

        if (!SettingsResetRecoveryService.TryCreateSnapshot(_settings))
        {
            LocalizedMessageBox.Show(_window, LocalizationService.Get(LocalizationKey.ProfileResetSnapshotCreateFailedFull),
                LocalizationService.Get(LocalizationKey.ProfileResetCancelledTitle), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        _owner.ResetAllUserData();
        LocalizedMessageBox.Show(_window,
            "Данные очищены. Для полного применения стандартных настроек перезапустите Lumisense.",
            "Сброс завершён", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        _window.Close();
        _owner.ShowSettingsWindow("Profile");
    }

    private void RefreshResetRecoveryButton() =>
        RestoreResetSnapshotButton.IsEnabled = SettingsResetRecoveryService.HasRecoverySnapshot;
}
