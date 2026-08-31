using System;
using System.IO;
using Microsoft.Win32;
using Velopack.Windows;

namespace Lumisense;

/// <summary>
/// Repairs autostart and the "Open in Lumisense" context menu before the legacy Inno Setup
/// uninstaller removes the old EXE install and its registry entries.
/// Does not touch the default file association — only the custom context menu command.
/// </summary>
internal static class LegacyIntegrationRepairService
{
    // Same key name the legacy Inno Setup installer used, to avoid a duplicate menu entry.
    private const string ContextMenuKeyName = "LumisenseOpen";
    private const string FileTypeProgId = "Lumisense.AudioFile";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lumisense";
    private static readonly string[] SupportedAudioExtensions = [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma"];

    /// <summary>
    /// Выполняет только после подтверждённого удаления legacy Inno Setup-копии. Очерёдность
    /// важна: старый uninstaller может удалить собственные ярлыки и registry values, поэтому
    /// восстанавливать их до его завершения недостаточно.
    /// </summary>
    public static void RepairAfterLegacyCleanup(string legacyInstallDir)
    {
        RepairAutostartIfPointingToLegacyInstall(legacyInstallDir);
        RegisterOpenInLumisenseContextMenu();
        RegisterLumisenseAsOpenWithHandler();
        RestoreVelopackShortcuts();
    }

    /// <summary>
    /// Repoints autostart to the current MSI copy if it currently points inside the legacy
    /// EXE install directory; otherwise does nothing.
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
    /// Registers a per-user (no admin required) "Open in Lumisense" context menu command
    /// for the current MSI copy. Safe to call repeatedly.
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

    /// <summary>
    /// Возвращает Lumisense в список «Открыть с помощью» для форматов, чьи Inno Setup registry
    /// values может удалить старый uninstaller. Не записывает default value расширения и потому
    /// не меняет приложение по умолчанию, выбранное пользователем в Windows.
    /// </summary>
    private static void RegisterLumisenseAsOpenWithHandler()
    {
        try
        {
            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath)) return;

            using RegistryKey? fileTypeKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{FileTypeProgId}", writable: true);
            using RegistryKey? iconKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{FileTypeProgId}\DefaultIcon", writable: true);
            using RegistryKey? commandKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{FileTypeProgId}\shell\open\command", writable: true);
            iconKey?.SetValue("", $"{currentExePath},0");
            commandKey?.SetValue("", $"\"{currentExePath}\" \"%1\"");

            foreach (string extension in SupportedAudioExtensions)
            {
                using RegistryKey? openWithKey = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{extension}\OpenWithProgids", writable: true);
                openWithKey?.SetValue(FileTypeProgId, string.Empty, RegistryValueKind.String);
            }

            Logger.Info("Lumisense зарегистрирован в «Открыть с помощью» для текущей MSI-копии без смены default app.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось восстановить регистрацию Lumisense в «Открыть с помощью»: {ex.Message}");
        }
    }

    private static void RestoreVelopackShortcuts()
    {
        try
        {
#pragma warning disable CS0618 // Velopack 1.2.0 exposes this API for explicit repair scenarios.
            var shortcuts = new Shortcuts();
            shortcuts.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot);
#pragma warning restore CS0618
            Logger.Info("Восстановлен ярлык Start Menu текущей MSI/Velopack-копии после legacy cleanup; Desktop shortcut сохраняется только по выбору пользователя.");
        }
        catch (Exception ex)
        {
            // Отсутствие ярлыка не должно отменять уже завершённое удаление: программа, settings
            // и обновления остаются рабочими, а пользователь может запустить Lumisense через
            // поиск Windows или из установленной MSI-копии.
            Logger.Warn($"Не удалось восстановить ярлыки MSI/Velopack после legacy cleanup: {ex.Message}");
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

        // Unquoted values may have trailing arguments; keep only the path token.
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
