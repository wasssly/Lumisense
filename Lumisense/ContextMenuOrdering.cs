using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lumisense;

// Порядок пунктов контекстных меню (AppSettings.ContextMenuOrder). Пункты находятся по Tag-идентификатору, поэтому порядок
// не зависит от языка и от того, какие пункты скрыты в настройках.
internal static class ContextMenuOrdering
{
    public const string Track = "Track";
    public const string Mini = "Mini";
    public const string Island = "Island";

    // Порядок по умолчанию совпадает с порядком пунктов в XAML каждого меню.
    public static readonly IReadOnlyDictionary<string, string[]> Defaults = new Dictionary<string, string[]>
    {
        [Track] = ["PlayNext", "AddToQueue", "FindFile", "Favorite", "ShowInExplorer", "CopyTrackName", "CopyPath", "CopyFile",
                   "ExportProcessedCopy", "Properties", "EditTags", "NormalizeFileName", "RemoveFromPlaylist", "DeleteFromDisk"],
        [Mini] = ["Settings", "NowPlaying", "Pin", "Topmost", "OverlayCompatibility", "SecondaryButton", "PlaybackRate", "Pitch",
                  "Opacity", "SnapToEdges", "ShowProgress", "ShowArtworkProgress", "ArtworkStyle", "ButtonsLayout"],
        [Island] = ["PlayPause", "Next", "Previous", "Hide", "Expand", "NowPlaying", "Speed", "Pitch", "Overlay", "Settings"],
    };

    // Неизвестные меню и пункты отбрасываются: файл настроек могли править вручную или он пришёл из другой версии.
    public static Dictionary<string, List<string>> Normalize(Dictionary<string, List<string>>? source)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (source is null) return result;

        foreach (var pair in source)
        {
            if (pair.Value is null || !Defaults.TryGetValue(pair.Key, out string[]? known)) continue;
            List<string> ids = [.. pair.Value.Where(id => id is not null && known.Contains(id)).Distinct()];
            if (ids.Count > 0) result[pair.Key] = ids;
        }

        return result;
    }

    // Пустой сохранённый порядок — порядок по умолчанию.
    public static IList<string> Effective(AppSettings settings, string menu) =>
        settings.ContextMenuOrder is { } all && all.TryGetValue(menu, out var saved) && saved.Count > 0
            ? saved
            : Defaults[menu];

    // Пункты с идентификатором занимают прежние «ячейки» в новом порядке; всё без идентификатора (разделители,
    // служебные пункты) остаётся на своих местах. Пункты, которых нет в order, идут после известных в прежнем порядке.
    public static List<T> Arrange<T>(IReadOnlyList<T> items, Func<T, string?> idOf, IList<string> order)
    {
        var slots = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            if (idOf(items[i]) is { Length: > 0 }) slots.Add(i);
        }

        var sorted = slots.Select(i => items[i])
            .OrderBy(item =>
            {
                int index = order.IndexOf(idOf(item)!);
                return index < 0 ? int.MaxValue : index;
            })
            .ToList();

        var result = items.ToList();
        for (int k = 0; k < slots.Count; k++) result[slots[k]] = sorted[k];
        return result;
    }

    public static void Apply(ItemsControl menu, IList<string> order)
    {
        var items = menu.Items.Cast<object>().ToList();
        var arranged = Arrange(items, item => (item as FrameworkElement)?.Tag as string, order);
        if (!arranged.SequenceEqual(items))
        {
            menu.Items.Clear();
            foreach (object item in arranged) menu.Items.Add(item);
        }

        RefreshSeparators(menu);
    }

    public static void Apply(Panel panel, IList<string> order)
    {
        var items = panel.Children.Cast<UIElement>().ToList();
        var arranged = Arrange(items, item => (item as FrameworkElement)?.Tag as string, order);
        if (arranged.SequenceEqual(items)) return;

        panel.Children.Clear();
        foreach (UIElement item in arranged) panel.Children.Add(item);
    }

    // Группы разделителей в XAML заданы фиксированными списками пунктов и после перестановки не совпадают с реальным
    // содержимым, поэтому разделитель показывается только между двумя видимыми пунктами и никогда не идёт подряд.
    private static void RefreshSeparators(ItemsControl menu)
    {
        bool seenVisibleItem = false;
        Separator? pending = null;
        foreach (object item in menu.Items)
        {
            if (item is Separator separator)
            {
                BindingOperations.ClearBinding(separator, UIElement.VisibilityProperty);
                separator.Visibility = Visibility.Collapsed;
                if (seenVisibleItem) pending = separator;
                continue;
            }

            if (item is UIElement { Visibility: Visibility.Visible })
            {
                if (pending is not null)
                {
                    pending.Visibility = Visibility.Visible;
                    pending = null;
                }

                seenVisibleItem = true;
            }
        }
    }
}
