using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumisense;

// Порядок пунктов контекстного меню, который пользователь меняет перетаскиванием на странице настроек.
internal static class ContextMenuOrderLink
{
    public static void Attach(System.Windows.Controls.Panel panel, string menu, AppSettings settings, Func<bool> isInitializing)
    {
        PanelReorder.Sort(panel, ContextMenuOrdering.Effective(settings, menu));
        PanelReorder.Enable(panel, ids => Changed(settings, menu, ids, isInitializing));
    }

    public static void Changed(AppSettings settings, string menu, IReadOnlyList<string> ids, Func<bool> isInitializing)
    {
        if (isInitializing()) return;

        // Порядок по умолчанию не хранится: так новые пункты будущих версий попадут на свои места сами.
        if (ids.SequenceEqual(ContextMenuOrdering.Defaults[menu]))
            settings.ContextMenuOrder.Remove(menu);
        else
            settings.ContextMenuOrder[menu] = [.. ids];
        BackgroundTask.FireAndForget(SettingsManager.SaveAsync(settings), "SaveSettingsAsync");
    }

    public static void Reset(System.Windows.Controls.Panel panel, string menu, AppSettings settings)
    {
        PanelReorder.Sort(panel, ContextMenuOrdering.Defaults[menu]);
        Changed(settings, menu, PanelReorder.Ids(panel), () => false);
    }
}
