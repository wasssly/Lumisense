using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Lumisense;

// Локальные точки восстановления, создаваемые только перед явным сбросом пользователя.
// Это не профиль для передачи: снимок содержит полный settings.json (плейлист, избранное,
// статистику и пресеты), поэтому хранится исключительно в папке данных текущего пользователя.
internal static class SettingsResetRecoveryService
{
    private const int MaxSnapshots = 3;
    private static readonly string RecoveryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense", "reset-recovery");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 16 };

    public static bool HasRecoverySnapshot => EnumerateSnapshots().Any();

    public static bool TryCreateSnapshot(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(RecoveryDirectory);
            string snapshotPath = Path.Combine(RecoveryDirectory,
                $"before_reset_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}_{Guid.NewGuid():N}.json");
            string temporaryPath = snapshotPath + ".tmp";
            string json = JsonSerializer.Serialize(settings, JsonOptions);

            try
            {
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, snapshotPath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            PruneSnapshots();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Logger.Warn($"Не удалось создать точку восстановления перед сбросом: {ex.Message}");
            return false;
        }
    }

    // Возвращает наиболее свежий корректный снимок. Проверка проходит через тот же сервис
    // миграции/валидации, что и обычный settings.json, поэтому повреждённый backup не попадёт
    // в живое состояние приложения.
    public static bool TryRestoreLatest(AppSettings target)
    {
        foreach (string path in EnumerateSnapshots())
        {
            if (!SettingsIntegrityService.TryLoad(path, out AppSettings? snapshot, out string? failure) || snapshot is null)
            {
                Logger.Warn($"Пропущена некорректная точка восстановления {Path.GetFileName(path)}: {failure ?? "неизвестная ошибка"}");
                continue;
            }

            CopyCompleteSettings(snapshot, target);
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSnapshots()
    {
        try
        {
            return Directory.Exists(RecoveryDirectory)
                ? Directory.EnumerateFiles(RecoveryDirectory, "before_reset_*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось прочитать точки восстановления сброса: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static void PruneSnapshots()
    {
        foreach (string obsoletePath in EnumerateSnapshots().Skip(MaxSnapshots))
            TryDelete(obsoletePath);
    }

    // Снимок и target — разные объекты. Рефлексия здесь намеренно ограничена редкой операцией
    // восстановления полного settings.json: она сохраняет новые поля AppSettings без опасного
    // ручного списка, в отличие от переносимого .lumi-профиля с его явным allowlist.
    private static void CopyCompleteSettings(AppSettings source, AppSettings target)
    {
        foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(target, property.GetValue(source));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Не удалось удалить старую точку восстановления: {ex.Message}");
        }
    }
}
