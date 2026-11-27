using System.Windows.Input;

namespace Lumisense;

public partial class SettingsWindow
{
    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_lazyPages.TryGetValue("Hotkeys", out ILazySettingsPage? page)) ((HotkeysSettingsPage)page).HandlePreviewKeyDown(e);
    }
}
