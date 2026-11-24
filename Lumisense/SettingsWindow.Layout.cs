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

public partial class SettingsWindow
{
    private System.Windows.Controls.ComboBox[] ModernSlotCombos() =>
        [ModernSlot0Combo, ModernSlot1Combo, ModernSlot2Combo, ModernSlot3Combo];

    private void SelectModernSlotCombos(IReadOnlyList<string> slots)
    {
        var combos = ModernSlotCombos();
        for (int i = 0; i < combos.Length; i++) SelectComboByTag(combos[i], slots[i]);
    }

    // Кнопки по бокам от «Назад / Пуск / Вперёд» в новом виде: включение и выбор четырёх мест.
    private void ModernLayoutOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        // Список без выбранного пункта значит «ещё не заполнен» (окно в процессе загрузки), а не «Нет»: иначе слоты затираются пустыми.
        if (ModernSlotCombos().Any(combo => combo.SelectedItem is null)) return;

        _settings.ModernShowSideButtons = ModernShowSideButtonsCheckBox.IsChecked == true;
        ModernSlotsGrid.IsEnabled = _settings.ModernShowSideButtons;
        List<string> slots = MainWindow.NormalizeModernSlots(ModernSlotCombos().Select(combo => SelectedTag(combo, string.Empty)));
        _settings.ModernControlSlots = slots;

        // Повторно выбранная кнопка сбрасывается в «Нет»: показываем это в списках сразу.
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            SelectModernSlotCombos(slots);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void GlassControlsBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.GlassControlsBackground = GlassControlsBackgroundCheckBox.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ControlButtonsBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.ControlButtonsBackground = ControlButtonsBackgroundCheckBox.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayButtonStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlayButtonAccentBackground = PlayButtonAccentRadio.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MoreMenuStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.MoreMenuSpring = MoreMenuSpringRadio.IsChecked == true;
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayButtonShape_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.PlayButtonRound = PlayButtonRoundRadio.IsChecked == true;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void CompactCoverAlignment_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.CompactCoverAlignment = SelectedTag(CompactCoverAlignmentCombo, "Left");
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void MainWindowLayoutRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton { Tag: string layout }) return;

        _settings.MainWindowLayout = layout;
        _owner.ApplyMainWindowLayoutLive();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void PlayerViewModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is not System.Windows.Controls.RadioButton { Tag: string modeName }) return;
        _owner.SetPlayerViewModeByName(modeName);
    }
}
