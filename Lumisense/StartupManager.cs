using Microsoft.Win32;

namespace AudioPlayer;

/// <summary>
/// Автозапуск плеера вместе с Windows — через обычный пользовательский раздел реестра
/// HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run, тот же механизм, которым
/// пользуется абсолютное большинство обычных (не системных) приложений с настройкой "запускать
/// при входе в систему". Не требует прав администратора (HKCU — раздел текущего пользователя,
/// не HKLM) и не создаёt отдельную задачу в Планировщике заданий — для одного простого
/// чекбокса "запускать с Windows" это было бы явно избыточно.
///
/// Источник истины — САМ реестр, а не отдельное поле в settings.json: так чекбокс в настройках
/// не может разъехаться с реальным состоянием, даже если пользователь потом сам уберёт запись
/// через "Диспетчер задач → Автозагрузка" (там Windows тоже редактирует именно этот раздел
/// реестра) — при следующем открытии окна настроек чекбокс просто честно покажет то, что есть
/// на самом деле, вместо устаревшего "да" из settings.json.
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
                // Environment.ProcessPath (а не Assembly.Location!) — для single-file-сборки
                // (см. Lumisense.csproj) Assembly.Location всегда возвращает пустую строку, это
                // прямо отмечено и в TrayIconManager.cs. ProcessPath — надёжный способ узнать
                // реальный путь к запущенному .exe независимо от способа публикации/сборки.
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
