using System.Collections.Generic;
using System.Windows;

namespace Lumisense;

// Группы радиокнопок показаны выпадающим списком (Tag="RadioGroup"): выбор отмечает скрытую радиокнопку группы.
// Имена, обработчики и загрузка настроек остаются прежними.
public partial class SettingsWindow
{
    private bool _syncingRadioCombo;

    private void InitializeRadioCombos()
    {
        foreach (System.Windows.Controls.ComboBox combo in FindRadioCombos(this))
        {
            System.Windows.Controls.ComboBox owner = combo;
            foreach (System.Windows.Controls.RadioButton radio in RadioGroupMembers(owner))
                radio.Checked += (_, _) => SyncRadioCombo(owner);

            owner.SelectionChanged += RadioCombo_SelectionChanged;
            SyncRadioCombo(owner);
        }
    }

    private IEnumerable<System.Windows.Controls.RadioButton> RadioGroupMembers(System.Windows.Controls.ComboBox combo)
    {
        foreach (object entry in combo.Items)
        {
            if (entry is System.Windows.Controls.ComboBoxItem { Tag: string name }
                && FindName(name) is System.Windows.Controls.RadioButton radio)
                yield return radio;
        }
    }

    private static IEnumerable<System.Windows.Controls.ComboBox> FindRadioCombos(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is System.Windows.Controls.ComboBox { Tag: "RadioGroup" } combo) yield return combo;
            foreach (System.Windows.Controls.ComboBox nested in FindRadioCombos(node)) yield return nested;
        }
    }

    private void SyncRadioCombo(System.Windows.Controls.ComboBox combo)
    {
        System.Windows.Controls.ComboBoxItem? selected = null;
        foreach (object entry in combo.Items)
        {
            if (entry is System.Windows.Controls.ComboBoxItem { Tag: string name } item
                && FindName(name) is System.Windows.Controls.RadioButton { IsChecked: true })
            {
                selected = item;
                break;
            }
        }

        if (ReferenceEquals(combo.SelectedItem, selected)) return;

        _syncingRadioCombo = true;
        try { combo.SelectedItem = selected; }
        finally { _syncingRadioCombo = false; }
    }

    private void RadioCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingRadioCombo) return;

        if (sender is System.Windows.Controls.ComboBox { SelectedItem: System.Windows.Controls.ComboBoxItem { Tag: string name } }
            && FindName(name) is System.Windows.Controls.RadioButton radio)
            radio.IsChecked = true;
    }

    // Глобальные хоткеи по умолчанию не заданы, поэтому сброс всех — это очистка всех привязок (как кнопка «Очистить» у каждой).
    private void HotkeysResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRecording();

        foreach (HotkeyTarget target in System.Enum.GetValues<HotkeyTarget>())
        {
            if (target == HotkeyTarget.None) continue;

            SetHotkeyBinding(target, new HotkeyBinding());
            RefreshHotkeyButtonText(target);
        }

        _owner.ReapplyHotkeys();
    }
}
