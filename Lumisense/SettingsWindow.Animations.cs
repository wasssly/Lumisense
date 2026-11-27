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
    // Меню «Трек» упорядочивает страница «Библиотека» (LibrarySettingsPage).
    private IEnumerable<(string Menu, System.Windows.Controls.Panel Panel)> ContextMenuOrderPanels() =>
    [
        (ContextMenuOrdering.Mini, MiniContextMenuActionsPanel),
        (ContextMenuOrdering.Island, IslandMenuActionsPanel),
    ];

    private void ContextMenuOrderChanged(string menu, IReadOnlyList<string> ids) =>
        ContextMenuOrderLink.Changed(_settings, menu, ids, () => _isInitializing);

    private void ContextMenuOrderReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string menu }) return;

        foreach (var (key, panel) in ContextMenuOrderPanels())
        {
            if (key == menu) ContextMenuOrderLink.Reset(panel, menu, _settings);
        }
    }

}
