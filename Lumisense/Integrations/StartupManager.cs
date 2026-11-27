using System.IO;
using Microsoft.Win32;

namespace Lumisense;

// Автозапуск через HKCU\...\Run — без прав админа и без задачи в Планировщике. Источник
// истины сам реестр, а не settings.json: правку через Диспетчер задач чекбокс подхватит.
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lumisense";

    // Метка запуска из автозагрузки. Windows запускает записи Run с задержкой после входа, и если пользователь успел открыть плеер
    // сам, этот запуск становится вторым процессом: без метки он переключал бы вид уже работающего окна (раскрывал остров или
    // мини-плеер в главное окно). С меткой лишний запуск тихо завершается (см. App.OnStartup).
    public const string AutostartArgument = "--autostart";

    internal static string BuildRunValue(string exePath) => $"\"{exePath}\" {AutostartArgument}";

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

                key.SetValue(RunValueName, BuildRunValue(exePath));
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

    // Ключ Run хранит путь на момент включения: после переноса папки или переустановки в другое место он остаётся
    // старым, и автозапуск молча перестаёт работать.
    public static void RepairStalePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not string runValue) return;

            string? currentExePath = System.Environment.ProcessPath;
            string? repaired = RepairRunValue(runValue, currentExePath, File.Exists);
            if (repaired is null) return;

            key.SetValue(RunValueName, repaired);
            Logger.Info("Автозапуск обновлён: путь или метка запуска из автозагрузки приведены к актуальному виду.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось проверить путь автозапуска: {ex.Message}");
        }
    }

    // Новое значение для ключа Run или null, если менять нечего: устаревший путь заменяется на текущий, а запись прежних версий
    // без метки автозапуска получает её с сохранением пути (в том числе портативной копии) и остальных аргументов.
    internal static string? RepairRunValue(string runValue, string? currentExePath, Func<string, bool> fileExists)
    {
        if (ShouldRepoint(runValue, currentExePath, fileExists)) return BuildRunValue(currentExePath!);

        if (LegacyIntegrationRepairService.ExtractExecutablePath(runValue) is null) return null;
        if (runValue.Contains(AutostartArgument, StringComparison.OrdinalIgnoreCase)) return null;

        return $"{runValue.TrimEnd()} {AutostartArgument}";
    }

    // Перепривязываем только если прежнего файла уже нет: иначе запуск второй копии (например, portable) увёл бы автозапуск.
    internal static bool ShouldRepoint(string runValue, string? currentExePath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(currentExePath)) return false;

        string? registeredPath = LegacyIntegrationRepairService.ExtractExecutablePath(runValue);
        return registeredPath is not null &&
               !string.Equals(registeredPath, currentExePath, StringComparison.OrdinalIgnoreCase) &&
               !fileExists(registeredPath);
    }
}
