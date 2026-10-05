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
        catch (Exception ex)
        {
            // Нет доступа к реестру и т.п. — считаем, что автозапуск не настроен, а не падаем
            Logger.Warn($"Не удалось прочитать состояние автозапуска: {ex.Message}");
            return false;
        }
    }

    /// <returns>false, если запись в реестр не удалась и состояние автозапуска не изменилось.</returns>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return false;

            if (enabled)
            {
                // ProcessPath, не Assembly.Location — для single-file-сборки Location всегда пустой
                string? exePath = System.Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;

                key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Политика, антивирус или нет прав на запись: вызывающий откатывает флажок.
            Logger.Warn($"Не удалось изменить автозапуск: {ex.Message}");
            return false;
        }
    }
}
