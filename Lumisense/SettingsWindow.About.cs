using System;

namespace Lumisense;

public partial class SettingsWindow
{
    internal static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Нет браузера по умолчанию и т.п. — не критично, но без записи в журнал нажатие выглядело бы поломкой.
            Logger.Warn($"Не удалось открыть ссылку {url}: {ex.Message}");
        }
    }
}
