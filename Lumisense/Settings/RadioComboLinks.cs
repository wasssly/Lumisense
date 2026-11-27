using System;
using System.Collections.Generic;
using System.Windows;

namespace Lumisense;

// Группы радиокнопок показаны выпадающим списком (Tag="RadioGroup"): выбор отмечает скрытую радиокнопку группы.
// Имена, обработчики и загрузка настроек остаются прежними. Имена ищет findName области, где лежит разметка:
// окно для обычных страниц, сама страница для ленивых.
internal sealed class RadioComboLinks
{
    private readonly Func<string, object?> _findName;
    private bool _syncing;

    private RadioComboLinks(Func<string, object?> findName) => _findName = findName;

    public static void Initialize(DependencyObject root, Func<string, object?> findName)
    {
        var links = new RadioComboLinks(findName);
        foreach (System.Windows.Controls.ComboBox combo in FindRadioCombos(root))
        {
            System.Windows.Controls.ComboBox owner = combo;
            foreach (System.Windows.Controls.RadioButton radio in links.RadioGroupMembers(owner))
                radio.Checked += (_, _) => links.Sync(owner);

            owner.SelectionChanged += links.OnSelectionChanged;
            links.Sync(owner);
        }
    }

    private IEnumerable<System.Windows.Controls.RadioButton> RadioGroupMembers(System.Windows.Controls.ComboBox combo)
    {
        foreach (object entry in combo.Items)
        {
            if (entry is System.Windows.Controls.ComboBoxItem { Tag: string name }
                && _findName(name) is System.Windows.Controls.RadioButton radio)
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

    private void Sync(System.Windows.Controls.ComboBox combo)
    {
        System.Windows.Controls.ComboBoxItem? selected = null;
        foreach (object entry in combo.Items)
        {
            if (entry is System.Windows.Controls.ComboBoxItem { Tag: string name } item
                && _findName(name) is System.Windows.Controls.RadioButton { IsChecked: true })
            {
                selected = item;
                break;
            }
        }

        if (ReferenceEquals(combo.SelectedItem, selected)) return;

        _syncing = true;
        try { combo.SelectedItem = selected; }
        finally { _syncing = false; }
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        if (sender is System.Windows.Controls.ComboBox { SelectedItem: System.Windows.Controls.ComboBoxItem { Tag: string name } }
            && _findName(name) is System.Windows.Controls.RadioButton radio)
            radio.IsChecked = true;
    }
}
