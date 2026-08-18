using System.IO;
using System.Text;

namespace AudioPlayer;

// Отдельный журнал Discord Rich Presence не содержит названий треков или путей к файлам: он
// предназначен для диагностики IPC, а не для дублирования пользовательских метаданных.
public static class DiscordRichPresenceLogger
{
    private const long MaxLogBytes = 1L * 1024 * 1024;
    private static readonly object Sync = new();

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumisense", "discord-rich-presence.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception exception) =>
        Write("ERROR", $"{message} | {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                string? directory = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                RotateIfNeeded();
                string line = $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Логирование не должно влиять на аудиопоток или UI, даже если профиль пользователя
            // недоступен для записи или журнал удерживается внешней программой.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length < MaxLogBytes) return;
            File.Move(LogFilePath, LogFilePath + ".1", overwrite: true);
        }
        catch
        {
            // Если ротация не удалась, следующая попытка записи безопасно обработается выше.
        }
    }
}
