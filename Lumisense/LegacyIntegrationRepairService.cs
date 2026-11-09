using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Velopack.Windows;

namespace Lumisense;

/// <summary>Repairs autostart and the "Open with" registration before the legacy Inno Setup uninstaller
/// removes the old EXE install and its registry entries; leaves the default file association alone.</summary>
internal static class LegacyIntegrationRepairService
{
    // Имя ключа пункта «Открыть в Lumisense», который регистрировали прежние версии; сейчас он только удаляется.
    private const string ContextMenuKeyName = "LumisenseOpen";
    private const string FileTypeProgId = "Lumisense.AudioFile";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lumisense";
    private static readonly string[] SupportedAudioExtensions = AudioFormats.Extensions;

    /// <summary>Выполняется только после подтверждённого удаления legacy-копии; порядок важен: старый uninstaller
    /// может удалить ярлыки и registry values, поэтому восстановление до его завершения недостаточно.</summary>
    public static void RepairAfterLegacyCleanup(string legacyInstallDir)
    {
        RepairAutostartIfPointingToLegacyInstall(legacyInstallDir);
        RegisterLumisenseAsOpenWithHandler();
        RestoreVelopackShortcuts();
    }

    /// <summary>Repoints autostart to the current MSI copy if it points into the legacy EXE install directory.</summary>
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

    /// <summary>True only the first time on this machine; the caller must save the settings before running the cleanup.</summary>
    public static bool TryClaimLegacyHklmContextMenuCleanup(AppSettings settings)
    {
        if (settings.HklmContextMenuCleanupAttempted) return false;

        // Флаг ставится в объекте главного окна: отдельная копия настроек затиралась бы его checkpoint-сохранением.
        settings.HklmContextMenuCleanupAttempted = true;
        return true;
    }

    /// <summary>Пункт «Открыть в Lumisense» от прежних установщиков лежит в HKLM (как у wildcard-ключа, так и у
    /// SystemFileAssociations\{расширение}); удалить его без повышения прав нельзя, поэтому запрашиваем UAC один раз.</summary>
    public static void CleanupLegacyHklmContextMenu()
    {
        try
        {
            var existing = new List<string>();
            foreach (string path in LegacyHklmContextMenuKeyPaths())
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
                if (key is not null) existing.Add(path);
            }

            if (existing.Count == 0) return;

            Logger.Warn($"Найдены унаследованные HKLM-ключи пункта «Открыть в Lumisense» ({existing.Count}) — запрашивается повышение прав для удаления.");
            string commands = string.Join(" & ", existing.Select(path => $@"reg delete ""HKLM\{path}"" /f"));
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c {commands}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            // Пользователь мог отклонить UAC — settings уже помечены, повторов не будет.
            Logger.Warn($"Не удалось удалить унаследованные HKLM-ключи контекстного меню: {ex.Message}");
        }
    }

    private static IEnumerable<string> LegacyHklmContextMenuKeyPaths()
    {
        yield return $@"Software\Classes\*\shell\{ContextMenuKeyName}";
        foreach (string extension in SupportedAudioExtensions)
        {
            yield return $@"Software\Classes\SystemFileAssociations\{extension}\shell\{ContextMenuKeyName}";
            yield return $@"Software\Classes\{extension}\shell\{ContextMenuKeyName}";
        }
    }

    /// <summary>Возвращает Lumisense в «Открыть с помощью» для форматов, чьи Inno Setup registry values мог удалить
    /// старый uninstaller; default value расширения не пишет, поэтому приложение по умолчанию не меняется.</summary>
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
            // Отсутствие ярлыка не отменяет завершённое удаление: Lumisense запускается через поиск Windows или из MSI-копии.
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
