using System;
using System.IO;

namespace AudioPlayer;

/// <summary>
/// Минимальный маркер первого обычного запуска после установки Velopack.
/// Маркер хранится рядом с пользовательскими данными, а не в каталоге приложения:
/// Velopack заменяет каталог current при обновлении, а legacy Inno Setup может быть
/// удалён пользователем только после проверки новой установки.
/// </summary>
internal static class VelopackMigrationLifecycle
{
    private const string MarkerFileName = "velopack-first-run.marker";

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense",
        "migration",
        MarkerFileName);

    /// <summary>
    /// Вызывается Velopack только при первом запуске после установки. Никакой UI или удаления
    /// здесь нет: OnFirstRun выполняется в контексте install lifecycle, а обычный UI появится
    /// только после того, как MainWindow успешно создан.
    /// </summary>
    public static void MarkFirstVelopackRun(string version)
    {
        try
        {
            string? directory = Path.GetDirectoryName(MarkerPath);
            if (string.IsNullOrWhiteSpace(directory)) return;

            Directory.CreateDirectory(directory);
            File.WriteAllText(MarkerPath, $"version={version}{Environment.NewLine}createdUtc={DateTime.UtcNow:O}");
        }
        catch (Exception ex)
        {
            // Не препятствуем первому запуску установленного приложения из-за необязательного
            // информационного маркера. Логгер может быть ещё не инициализирован.
            System.Diagnostics.Debug.WriteLine($"Could not create Velopack migration marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает true ровно один раз после первого запуска Velopack, после чего удаляет marker.
    /// Если удалить файл не удалось, он останется и уведомление может повториться, но данные
    /// пользователя и способность к обновлению от этого не затрагиваются.
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
