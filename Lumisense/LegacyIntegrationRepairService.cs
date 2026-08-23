using System;
using System.IO;
using Microsoft.Win32;

namespace AudioPlayer;

/// <summary>
/// Repairs autostart and the "Open in Lumisense" context menu before the legacy Inno Setup
/// uninstaller removes the old EXE install and its registry entries.
/// Does not touch the default file association — only the custom context menu command.
/// </summary>
internal static class LegacyIntegrationRepairService
{
    // Same key name the legacy Inno Setup installer used, to avoid a duplicate menu entry.
    private const string ContextMenuKeyName = "LumisenseOpen";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lumisense";

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
