using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Lumisense;

/// <summary>
/// Находит именно историческую Inno Setup-установку Lumisense и запускает её собственный
/// интерактивный деинсталлятор. Сервис не удаляет файлы, ключи реестра или пользовательские
/// данные сам: Inno Setup показывает свой штатный мастер, где пользователь сохраняет
/// %AppData%\Lumisense для новой MSI-версии.
/// </summary>
internal static class LegacyInnoCleanupService
{
    // Должен совпадать с фиксированным AppId в Installer/Lumisense.iss. Inno Setup добавляет
    // к этому значению суффикс _is1 в ключе uninstall, поэтому поиск не полагается на DisplayName.
    private const string LegacyInnoAppId = "{B7D9F8B4-3E36-4B6C-9B7A-2E9B7B7C0B41}";
    private const string UninstallRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + LegacyInnoAppId + "_is1";

    internal sealed record LegacyInnoInstall(string UninstallerPath);

    /// <summary>
    /// Возвращает legacy installation только при точном совпадении с AppId Lumisense и при
    /// наличии локального unins*.exe. Сторонние приложения и произвольные UninstallString
    /// никогда не запускаются.
    /// </summary>
    public static bool TryFind(out LegacyInnoInstall? legacyInstall)
    {
        legacyInstall = null;

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? uninstallKey = baseKey.OpenSubKey(UninstallRegistryPath, writable: false);
                string? uninstallerPath = GetVerifiedUninstallerPath(uninstallKey?.GetValue("UninstallString") as string);
                if (uninstallerPath is null) continue;

                legacyInstall = new LegacyInnoInstall(uninstallerPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.Warn($"Не удалось проверить legacy EXE-установку Lumisense: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// Открывает legacy Inno Setup uninstaller в обычном интерактивном режиме. Не используем
    /// /SILENT или /SUPPRESSMSGBOXES: пользователь должен увидеть штатный вопрос Inno Setup и
    /// сохранить общую папку %AppData%\Lumisense, выбрав «Нет».
    /// </summary>
    public static bool TryStartInteractiveUninstall(
        LegacyInnoInstall legacyInstall,
        out Process? uninstallerProcess,
        out string? technicalError)
    {
        uninstallerProcess = null;
        technicalError = null;
        try
        {
            if (!IsVerifiedUninstallerPath(legacyInstall.UninstallerPath))
            {
                technicalError = "Не удалось подтвердить путь к legacy-деинсталлятору.";
                return false;
            }

            uninstallerProcess = Process.Start(new ProcessStartInfo(legacyInstall.UninstallerPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            if (uninstallerProcess is null)
            {
                technicalError = "Windows не вернула процесс деинсталлятора.";
                return false;
            }

            Logger.Info("Запущен интерактивный legacy Inno Setup-деинсталлятор после MSI-миграции.");
            return true;
        }
        catch (Exception ex)
        {
            technicalError = ex.Message;
            Logger.Warn($"Не удалось запустить legacy Inno Setup-деинсталлятор: {ex.Message}");
            return false;
        }
    }

    internal static string? GetVerifiedUninstallerPath(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;

        string value = uninstallString.Trim();
        if (value.Length < 3 || value[0] != '"') return null;

        int closingQuote = value.IndexOf('"', 1);
        if (closingQuote <= 1 || !string.IsNullOrWhiteSpace(value[(closingQuote + 1)..])) return null;

        string candidate = value[1..closingQuote];
        return IsVerifiedUninstallerPath(candidate) ? candidate : null;
    }

    private static bool IsVerifiedUninstallerPath(string path)
    {
        try
        {
            string fileName = Path.GetFileName(path);
            return Path.IsPathFullyQualified(path) &&
                   fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                   fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
