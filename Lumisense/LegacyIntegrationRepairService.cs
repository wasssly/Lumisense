using System;
using System.IO;
using Microsoft.Win32;

namespace AudioPlayer;

/// <summary>
/// Восстанавливает пользовательские Windows-интеграции (автозапуск и контекстное меню
/// «Открыть в Lumisense») перед тем, как пользователь запустит legacy Inno Setup uninstaller
/// и его папка установки будет удалена.
///
/// Проблема: legacy-установщик регистрирует автозапуск (через сам EXE, см.
/// <see cref="StartupManager"/>) и правит HKCR-записи для контекстного меню «Открыть в
/// Lumisense» (см. Installer/Lumisense.iss, ключ "*\shell\LumisenseOpen"). Обе записи
/// либо указывают на legacy EXE, либо удаляются вместе с деинсталляцией — MSI-версия
/// сама по себе их не переносит и не восстанавливает.
///
/// Этот сервис намеренно НЕ трогает дефолтную ассоциацию файлов Windows (то, какая
/// программа открывает .mp3 по умолчанию) — это выбор пользователя, который нельзя менять
/// без явного действия в интерфейсе ОС. Восстанавливается только пользовательский пункт
/// правого клика, который явно возвращает интеграцию, потерянную из-за cleanup, а не
/// захватывает что-то новое.
/// </summary>
internal static class LegacyIntegrationRepairService
{
    // То же имя ключа, что использовал legacy Inno Setup — так после cleanup остаётся
    // ровно одна запись контекстного меню, а не задвоение старой и новой.
    private const string ContextMenuKeyName = "LumisenseOpen";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lumisense";

    /// <summary>
    /// Если автозапуск сейчас включён и указывает на исполняемый файл внутри старой
    /// EXE-установки, перепривязывает его на текущую (MSI) копию. Если автозапуск выключен
    /// или уже указывает на текущую копию — ничего не меняет, чтобы не трогать выбор
    /// пользователя без необходимости.
    /// </summary>
    public static void RepairAutostartIfPointingToLegacyInstall(string legacyInstallDir)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not string currentValue) return;

            string? registeredExePath = ExtractExecutablePath(currentValue);
            if (registeredExePath is null || !IsUnderDirectory(registeredExePath, legacyInstallDir))
                return;

            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath)) return;
            if (string.Equals(currentExePath, registeredExePath, StringComparison.OrdinalIgnoreCase))
                return;

            key.SetValue(RunValueName, $"\"{currentExePath}\"");
            Logger.Info("Автозапуск перепривязан с legacy EXE-копии Lumisense на текущую MSI-копию перед cleanup.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось проверить/перепривязать автозапуск при legacy cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// Регистрирует пользовательский (HKCU, без прав администратора) пункт контекстного меню
    /// «Открыть в Lumisense» для текущей MSI-копии. Идемпотентно — безопасно вызывать
    /// повторно, в том числе если запись уже существует и указывает на актуальный путь.
    /// </summary>
    public static void RegisterOpenInLumisenseContextMenu()
    {
        try
        {
            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath)) return;

            string label = LocalizationService.Get(LocalizationKey.UpdateLegacyCleanupContextMenuLabel);

            using RegistryKey? menuKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\*\shell\{ContextMenuKeyName}", writable: true);
            menuKey?.SetValue("", label);

            using RegistryKey? commandKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\*\shell\{ContextMenuKeyName}\command", writable: true);
            commandKey?.SetValue("", $"\"{currentExePath}\" \"%1\"");

            Logger.Info("Восстановлена команда контекстного меню «Открыть в Lumisense» для MSI-копии.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось зарегистрировать контекстное меню «Открыть в Lumisense»: {ex.Message}");
        }
    }

    internal static string? ExtractExecutablePath(string runValue)
    {
        string value = runValue.Trim();
        if (value.Length == 0) return null;

        if (value[0] == '"')
        {
            int closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        // Значения без кавычек в принципе допустимы для путей без пробелов — если после
        // пути есть аргументы, берём только первый "токен" до пробела.
        int firstSpace = value.IndexOf(' ');
        return firstSpace > 0 ? value[..firstSpace] : value;
    }

    internal static bool IsUnderDirectory(string filePath, string directory)
    {
        try
        {
            string fullFile = Path.GetFullPath(filePath);
            string fullDir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
