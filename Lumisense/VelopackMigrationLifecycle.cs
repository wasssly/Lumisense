using System;
using System.IO;
using Velopack.Windows;

namespace Lumisense;

/// <summary>
/// Неблокирующие marker-файлы для первого запуска и пользовательского выбора ярлыка
/// перед переходом на MSI/Velopack. Они хранятся вне каталога приложения, потому что
/// Velopack заменяет каталог current при обновлении.
/// </summary>
internal static class VelopackMigrationLifecycle
{
    private const string MarkerFileName = "velopack-first-run.marker";
    private const string DesktopShortcutPreferenceFileName = "velopack-desktop-shortcut.preference";

    private static string MigrationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense",
        "migration");

    private static string MarkerPath => Path.Combine(MigrationDirectory, MarkerFileName);
    private static string DesktopShortcutPreferencePath => Path.Combine(MigrationDirectory, DesktopShortcutPreferenceFileName);

    /// <summary>
    /// Вызывается Velopack только при первом запуске после установки. Никакого UI здесь нет.
    /// </summary>
    public static void MarkFirstVelopackRun(string version)
    {
        try
        {
            Directory.CreateDirectory(MigrationDirectory);
            File.WriteAllText(MarkerPath, $"version={version}{Environment.NewLine}createdUtc={DateTime.UtcNow:O}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not create Velopack migration marker: {ex.Message}");
        }
    }

    /// <summary>Сохраняет выбор пользователя до запуска MSI или Velopack update.</summary>
    public static void SaveDesktopShortcutPreference(bool createShortcut)
    {
        try
        {
            Directory.CreateDirectory(MigrationDirectory);
            File.WriteAllText(DesktopShortcutPreferencePath,
                createShortcut ? "create=true" : "create=false");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось сохранить выбор ярлыка рабочего стола: {ex.Message}");
        }
    }

    /// <summary>
    /// Применяет отложенный выбор после установки/обновления. Если пользователь разрешил
    /// ярлык, используется штатный Velopack API, а не ручная сборка .lnk-файла.
    /// </summary>
    public static void TryApplyPendingDesktopShortcutPreference()
    {
        try
        {
            if (!File.Exists(DesktopShortcutPreferencePath)) return;
            string value = File.ReadAllText(DesktopShortcutPreferencePath).Trim();
            File.Delete(DesktopShortcutPreferencePath);
            if (!value.Equals("create=true", StringComparison.OrdinalIgnoreCase))
            {
                RemoveKnownDesktopShortcuts();
                Logger.Info("Пользователь отказался от ярлыка Lumisense на рабочем столе.");
                return;
            }

#pragma warning disable CS0618 // Velopack 1.2.0 exposes this API for explicit shortcut repair/creation.
            new Shortcuts().CreateShortcutForThisExe(ShortcutLocation.Desktop);
#pragma warning restore CS0618
            Logger.Info("Создан ярлык Lumisense на рабочем столе по выбору пользователя.");
        }
        catch (Exception ex)
        {
            // Ошибка ярлыка не должна ломать запуск, обновление или воспроизведение.
            Logger.Warn($"Не удалось применить выбор ярлыка рабочего стола: {ex.Message}");
        }
    }

    private static void RemoveKnownDesktopShortcuts()
    {
        string[] paths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lumisense.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Lumisense.lnk")
        ];

        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Не удалось удалить ярлык Lumisense '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Возвращает true ровно один раз после первого запуска Velopack.
    /// </summary>
    public static bool TryConsumeFirstRunMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return false;
            File.Delete(MarkerPath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось обработать маркер первого запуска Velopack: {ex.Message}");
            return false;
        }
    }
}
