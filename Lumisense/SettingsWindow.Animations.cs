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
    private IEnumerable<(string Menu, System.Windows.Controls.Panel Panel)> ContextMenuOrderPanels() =>
    [
        (ContextMenuOrdering.Track, TrackContextMenuActionsPanel),
        (ContextMenuOrdering.Mini, MiniContextMenuActionsPanel),
        (ContextMenuOrdering.Island, IslandMenuActionsPanel),
    ];

    private void ContextMenuOrderChanged(string menu, IReadOnlyList<string> ids)
    {
        if (_isInitializing) return;

        // Порядок по умолчанию не хранится: так новые пункты будущих версий попадут на свои места сами.
        if (ids.SequenceEqual(ContextMenuOrdering.Defaults[menu]))
            _settings.ContextMenuOrder.Remove(menu);
        else
            _settings.ContextMenuOrder[menu] = [.. ids];
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    private void ContextMenuOrderReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string menu }) return;

        foreach (var (key, panel) in ContextMenuOrderPanels())
        {
            if (key != menu) continue;
            PanelReorder.Sort(panel, ContextMenuOrdering.Defaults[menu]);
            ContextMenuOrderChanged(menu, PanelReorder.Ids(panel));
        }
    }

    private IEnumerable<System.Windows.Controls.CheckBox> AnimationAreaBoxes() =>
        AnimationAreasPanel.Children.OfType<System.Windows.Controls.CheckBox>();

    private void AnimationAreaCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        SaveAnimationAreaSelection();
    }

    // Общая галочка: если включено всё — выключает всё, иначе включает всё. Само переключение WPF (три состояния) не нужно.
    private void AnimationsMasterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool enable = !AnimationAreaBoxes().All(box => box.IsChecked == true);
        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            foreach (var box in AnimationAreaBoxes()) box.IsChecked = enable;
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        SaveAnimationAreaSelection();
    }

    // «Снизить движение» хранится как раньше: он включён, когда выключены все разделы.
    private void SaveAnimationAreaSelection()
    {
        bool allOff = AnimationAreaBoxes().All(box => box.IsChecked != true);
        _settings.ReduceMotion = allOff;
        _settings.DisabledAnimationAreas = allOff
            ? []
            : AnimationAreas.Normalize(AnimationAreaBoxes().Where(box => box.IsChecked != true).Select(box => (string)box.Tag));
        RefreshAnimationsMasterCheckBox();
        _owner.ApplyAccessibilityPreferences();
    }

    private void RefreshAnimationsMasterCheckBox()
    {
        bool allOn = AnimationAreaBoxes().All(box => box.IsChecked == true);
        bool allOff = AnimationAreaBoxes().All(box => box.IsChecked != true);
        AnimationsMasterCheckBox.IsChecked = allOn ? true : allOff ? false : null;
    }
}
