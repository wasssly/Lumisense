using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

// Создаётся при первом показе страницы (см. SettingsWindow.EnsurePageBuilt).
public sealed partial class EqualizerSettingsPage : System.Windows.Controls.UserControl, ILazySettingsPage
{
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ISettingsHost _owner;

    // Глушит Changed-обработчики, пока значения читаются из настроек.
    private bool _isInitializing = true;

    internal EqualizerSettingsPage(Window window, AppSettings settings, ISettingsHost owner)
    {
        _window = window;
        _settings = settings;
        _owner = owner;

        // StaticResource разбирается до того, как элементы попадут в дерево окна, поэтому ресурсы окна подключаются заранее.
        Resources.MergedDictionaries.Add(window.Resources);
        InitializeComponent();

        EqualizerEnabledCheckBox.IsChecked = _owner.IsEqualizerEnabled;
        EqualizerAutoHeadroomCheckBox.IsChecked = _owner.IsEqualizerAutoHeadroom;
        DeviceSoundProfilesCheckBox.IsChecked = _owner.IsDeviceSoundProfilesEnabled;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            double gain = _owner.GetEqualizerBandGain(band);
            GetEqBandSlider(band).Value = gain;
            GetEqBandValueText(band).Text = FormatEqGain(gain);
        }
        RefreshPresetsList();
        _isInitializing = false;
    }

    public void MoveContentTo(System.Windows.Controls.Panel host) => LazySettingsPage.MoveChildren(PageEqualizer, host);

    public FrameworkElement? FindElement(string name) => FindName(name) as FrameworkElement;

    private void EqualizerEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetEqualizerEnabled(EqualizerEnabledCheckBox.IsChecked == true);
    }

    private void DeviceSoundProfilesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetDeviceSoundProfilesEnabled(DeviceSoundProfilesCheckBox.IsChecked == true);
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Профиль нового устройства изменил эквалайзер: обновляем открытые элементы.
    public void RefreshFromOwner()
    {
        EqualizerEnabledCheckBox.IsChecked = _owner.IsEqualizerEnabled;
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
        {
            double gain = _owner.GetEqualizerBandGain(band);
            GetEqBandSlider(band).Value = gain;
            GetEqBandValueText(band).Text = FormatEqGain(gain);
        }
    }

    public void RefreshBandTexts()
    {
        for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            GetEqBandValueText(band).Text = FormatEqGain(_owner.GetEqualizerBandGain(band));
    }

    private void EqualizerAutoHeadroomCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _owner.SetEqualizerAutoHeadroom(EqualizerAutoHeadroomCheckBox.IsChecked == true);
    }

    // Общий обработчик для всех 10 слайдеров полос — номер полосы передаётся через Tag
    // (см. SettingsWindow.xaml), а не десятью одинаковыми по сути методами.
    private void EqualizerBandSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.Slider { Tag: string tagStr } slider) return;
        if (!int.TryParse(tagStr, out int band)) return;

        GetEqBandValueText(band).Text = FormatEqGain(slider.Value);
        _owner.SetEqualizerBandGain(band, slider.Value);
    }

    // WPF не прокручивает Slider колесом сам: шаг — SmallChange за деление; Value поднимет EqualizerBandSlider_ValueChanged
    // (текст и звук, как при перетаскивании), а e.Handled не даёт листать страницу настроек.
    private void EqualizerBandSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.Slider slider) return;

        double step = slider.SmallChange > 0 ? slider.SmallChange : 0.5;
        double newValue = slider.Value + Math.Sign(e.Delta) * step;
        slider.Value = Math.Clamp(newValue, slider.Minimum, slider.Maximum);

        e.Handled = true;
    }

    private void EqualizerResetButton_Click(object sender, RoutedEventArgs e)
    {
        _owner.ResetEqualizer();

        // _isInitializing глушит ValueChanged при сбросе слайдеров в 0: ResetEqualizer уже применил всё разом,
        // а десять отдельных SetEqualizerBandGain были бы лишними.
        bool wasInitializing8 = _isInitializing;
        _isInitializing = true;
        try
        {
            for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            {
                GetEqBandSlider(band).Value = 0;
                GetEqBandValueText(band).Text = FormatEqGain(0);
            }
        }
        finally
        {
            _isInitializing = wasInitializing8;
        }
    }

    private static string FormatEqGain(double gainDb)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(
            LocalizationService.IsEnglish ? "en-US" : "ru-RU");
        string unit = LocalizationService.IsEnglish ? "dB" : "дБ";
        return $"{(gainDb > 0 ? "+" : "")}{gainDb.ToString("0.#", culture)} {unit}";
    }

    private void RefreshPresetsList()
    {
        string? previouslySelected = (EqualizerPresetComboBox.SelectedItem as EqualizerPreset)?.Name;

        EqualizerPresetComboBox.ItemsSource = null;
        EqualizerPresetComboBox.ItemsSource = _owner.EqualizerPresets;

        if (previouslySelected != null)
            EqualizerPresetComboBox.SelectedItem = _owner.EqualizerPresets.FirstOrDefault(p => p.Name == previouslySelected);

        UpdateEqualizerPresetButtonsState();
    }

    private void UpdateEqualizerPresetButtonsState()
    {
        bool hasSelection = EqualizerPresetComboBox.SelectedItem != null;
        EqualizerApplyPresetButton.IsEnabled = hasSelection;
        EqualizerDeletePresetButton.IsEnabled = hasSelection;
        EqualizerExportPresetButton.IsEnabled = hasSelection;
    }

    private void EqualizerPresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateEqualizerPresetButtonsState();

    private void EqualizerSavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedName = (EqualizerPresetComboBox.SelectedItem as EqualizerPreset)?.Name ?? "";
        var dialog = new TextInputDialog("Сохранить пресет", "Название пресета:", selectedName, _settings) { Owner = _window };
        if (dialog.ShowDialog() != true || dialog.ResultText.Length == 0) return;

        _owner.SaveEqualizerPreset(dialog.ResultText);
        RefreshPresetsList();
        EqualizerPresetComboBox.SelectedItem = _owner.EqualizerPresets.FirstOrDefault(p => p.Name == dialog.ResultText.Trim());
    }

    private void EqualizerApplyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        _owner.ApplyEqualizerPreset(preset);

        // Пресет применился в MainWindow — подтягиваем актуальные значения обратно в слайдеры,
        // так же как при обычном открытии окна (см. _isInitializing выше).
        bool wasInitializing9 = _isInitializing;
        _isInitializing = true;
        try
        {
            for (int band = 0; band < EqualizerSampleProvider.BandFrequencies.Length; band++)
            {
                double gain = _owner.GetEqualizerBandGain(band);
                GetEqBandSlider(band).Value = gain;
                GetEqBandValueText(band).Text = FormatEqGain(gain);
            }
        }
        finally
        {
            _isInitializing = wasInitializing9;
        }

        if (EqualizerEnabledCheckBox.IsChecked != true)
        {
            EqualizerEnabledCheckBox.IsChecked = true;
            _owner.SetEqualizerEnabled(true);
        }
    }

    private void EqualizerDeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        var confirm = LocalizedMessageBox.Show(
            _window,
            $"Удалить пресет \"{preset.Name}\"?",
            "Удаление пресета",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        _owner.DeleteEqualizerPreset(preset);
        RefreshPresetsList();
    }

    // "Поделиться" — сохраняет выбранный пресет в отдельный .json-файл, который можно переслать
    // как обычный файл (мессенджер, почта, флешка); получатель добавляет его через "Импортировать".
    private void EqualizerExportPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (EqualizerPresetComboBox.SelectedItem is not EqualizerPreset preset) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.Translate("Поделиться пресетом эквалайзера"),
            Filter = LocalizationService.Translate("Пресет эквалайзера (*.json)|*.json"),
            FileName = $"{preset.Name}.json"
        };
        if (dialog.ShowDialog(_window) != true) return;

        try
        {
            _owner.ExportEqualizerPreset(preset, dialog.FileName);
        }
        catch (Exception ex)
        {
            LocalizedMessageBox.Show(_window, $"Не удалось сохранить пресет:\n{ex.Message}", "Ошибка",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void EqualizerImportPresetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Translate("Импортировать пресет эквалайзера"),
            Filter = LocalizationService.Translate("Пресет эквалайзера (*.json)|*.json|Все файлы (*.*)|*.*")
        };
        if (dialog.ShowDialog(_window) != true) return;

        var imported = _owner.ImportEqualizerPresetFromFile(dialog.FileName);
        if (imported == null)
        {
            LocalizedMessageBox.Show(_window, "Не удалось прочитать пресет — файл повреждён или это не пресет Lumisense.",
                "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        RefreshPresetsList();
        EqualizerPresetComboBox.SelectedItem = imported;
    }

    private System.Windows.Controls.Slider GetEqBandSlider(int band) => band switch
    {
        0 => EqBand0Slider,
        1 => EqBand1Slider,
        2 => EqBand2Slider,
        3 => EqBand3Slider,
        4 => EqBand4Slider,
        5 => EqBand5Slider,
        6 => EqBand6Slider,
        7 => EqBand7Slider,
        8 => EqBand8Slider,
        _ => EqBand9Slider
    };

    private System.Windows.Controls.TextBlock GetEqBandValueText(int band) => band switch
    {
        0 => EqBand0ValueText,
        1 => EqBand1ValueText,
        2 => EqBand2ValueText,
        3 => EqBand3ValueText,
        4 => EqBand4ValueText,
        5 => EqBand5ValueText,
        6 => EqBand6ValueText,
        7 => EqBand7ValueText,
        8 => EqBand8ValueText,
        _ => EqBand9ValueText
    };
}
