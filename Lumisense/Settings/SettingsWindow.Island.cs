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
    // Заметка «остров не используется» лежит на странице острова, а вид мини-плеера выбирают на странице мини-плеера.
    internal void UpdateIslandInactiveNote()
    {
        if (_lazyPages.TryGetValue("Island", out ILazySettingsPage? page)) ((IslandSettingsPage)page).UpdateInactiveNote();
    }

    internal void RefreshIslandMonitorCombo()
    {
        if (_lazyPages.TryGetValue("Island", out ILazySettingsPage? page)) ((IslandSettingsPage)page).RefreshMonitorCombo();
    }

    internal static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == tag) ?? combo.Items[0];

    internal static string SelectedTag(System.Windows.Controls.ComboBox combo, string fallback) =>
        combo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : fallback;

}
