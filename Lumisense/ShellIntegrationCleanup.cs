using System;
using Microsoft.Win32;

namespace Lumisense;

/// <summary>Убирает записи реестра, которые плеер создаёт сам (Velopack-установка не делает этого за него): ассоциацию
/// Lumisense.AudioFile, значения «Открыть с помощью» и автозапуск. Без этого после удаления Windows продолжала бы предлагать
/// несуществующую программу по умолчанию. Также убирает пункт «Открыть в Lumisense», который прежние версии добавляли в
/// контекстное меню (сейчас он не регистрируется).</summary>
internal static class ShellIntegrationCleanup
{
    private const string ProgId = "Lumisense.AudioFile";
    private const string ContextMenuKeyName = "LumisenseOpen";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Lumisense";

    /// <summary>Вызывается из хука удаления Velopack, где нет ни журнала, ни интерфейса: каждый шаг независим, ошибки глотаются.</summary>
    public static void RemoveAll()
    {
        TryDeleteTree($@"Software\Classes\{ProgId}");
        RemoveContextMenuEntries();

        foreach (string extension in AudioFormats.Extensions)
            TryDeleteValue($@"Software\Classes\{extension}\OpenWithProgids", ProgId);

        RemoveAutostartPointingToThisInstall();
    }

    /// <summary>Per-user записи пункта «Открыть в Lumisense» (wildcard-ключ и по расширениям); безопасно вызывать при каждом запуске.</summary>
    public static void RemoveContextMenuEntries()
    {
        TryDeleteTree($@"Software\Classes\*\shell\{ContextMenuKeyName}");

        foreach (string extension in AudioFormats.Extensions)
        {
            TryDeleteTree($@"Software\Classes\{extension}\shell\{ContextMenuKeyName}");
            TryDeleteTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\{ContextMenuKeyName}");
        }
    }

    // Автозапуск снимается, только если он указывает в папку удаляемой копии: запись другой установки не трогаем.
    private static void RemoveAutostartPointingToThisInstall()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not string value) return;

            string? exePath = LegacyIntegrationRepairService.ExtractExecutablePath(value);
            if (exePath is null || !LegacyIntegrationRepairService.IsUnderDirectory(exePath, AppContext.BaseDirectory)) return;

            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Autostart cleanup skipped: {ex.Message}");
        }
    }

    private static void TryDeleteTree(string subKeyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Registry cleanup skipped for {subKeyPath}: {ex.Message}");
        }
    }

    private static void TryDeleteValue(string subKeyPath, string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Registry cleanup skipped for {subKeyPath}: {ex.Message}");
        }
    }
}
