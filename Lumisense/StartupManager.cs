using Microsoft.Win32;

namespace Lumisense;

// Автозапуск через HKCU\...\Run — без прав админа и без задачи в Планировщике. Источник
// истины сам реестр, а не settings.json: правку через Диспетчер задач чекбокс подхватит.
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lumisense";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string;
        }
        catch
        {
            // Нет доступа к реестру и т.п. — считаем, что автозапуск не настроен, а не падаем
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                // ProcessPath, не Assembly.Location — для single-file-сборки Location всегда пустой
                string? exePath = System.Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;

                key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Нет прав на запись в реестр и т.п. — тихо игнорируем, как и остальные подобные
            // ситуации в этом проекте (см. AppSettings.Save)
        }
    }
}
