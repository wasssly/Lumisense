using System.Linq;
using System.Windows.Controls;

namespace Lumisense;

internal static class SettingsMonitorCombo
{
    // Вызывающий сам глушит Changed-обработчики: Items.Clear и SelectedItem вызывают SelectionChanged.
    public static void Fill(ComboBox combo, string savedMonitor, string autoText)
    {
        combo.Items.Clear();

        var autoItem = new ComboBoxItem { Content = LocalizationService.Translate(autoText), Tag = "" };
        combo.Items.Add(autoItem);

        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string label = LocalizationService.Translate($"Монитор {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}") +
                (s.Primary ? LocalizationService.Translate(" (основной)") : "");
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = s.DeviceName });
        }

        var selected = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == savedMonitor);
        combo.SelectedItem = selected ?? autoItem;
    }
}
