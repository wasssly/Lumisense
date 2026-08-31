using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumisense;

// Файловый журнал для локальной диагностики. Он не отправляется по сети, но может быть
// приложен к issue, поэтому перед записью удаляются персональные сегменты абсолютных путей.
// Ограничения по размеру защищают папку %AppData% от неограниченного роста при долгой работе.
public static class Logger
{
    private static readonly string LogsDir = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Lumisense", "logs");

    private static readonly string SessionStamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    private static string _logFilePath = BuildLogFilePath(0);

    private static readonly object Lock = new();
    private static bool _initialized;
    private static bool _initFailed;
    private static int _currentLogPart;

    private const int MaxLogFiles = 50;
    private const long MaxSingleLogBytes = 1L * 1024 * 1024;
    private const long MaxTotalLogBytes = 16L * 1024 * 1024;
    private const int MaxMessageCharacters = 12_000;

    private static readonly Regex UserProfilePathRegex = new(
        @"(?i)([A-Z]:\\Users\\)([^\\\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex NetworkPathRegex = new(
        @"(?<![A-Za-z0-9])\\\\[^\\\s\r\n]+(?:\\[^\\\s\r\n]+)*", RegexOptions.Compiled);

    private static string BuildLogFilePath(int part) => Path.Combine(
        LogsDir,
        part == 0 ? $"lumisense_{SessionStamp}.log" : $"lumisense_{SessionStamp}_part-{part:D2}.log");

    private static void EnsureInitialized()
    {
        if (_initialized || _initFailed) return;

        try
        {
            Directory.CreateDirectory(LogsDir);
            PruneOldLogs();
            _initialized = true;
        }
        catch
        {
            // Отсутствие прав/места на диске не должно мешать самому плееру.
            _initFailed = true;
        }
    }

    private static void PruneOldLogs()
    {
        var files = new DirectoryInfo(LogsDir).GetFiles("lumisense_*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var file in files.Skip(MaxLogFiles))
            TryDelete(file);

        files = new DirectoryInfo(LogsDir).GetFiles("lumisense_*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        long totalBytes = files.Sum(file => file.Length);
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (totalBytes <= MaxTotalLogBytes) break;
            long length = file.Length;
            if (TryDelete(file)) totalBytes -= length;
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch
        {
            // Занятый файл будет снова рассмотрен при следующем запуске/ротации.
            return false;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, System.Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        string safeMessage = SanitizeForLog(message);
        string line = $"{System.DateTime.Now:HH:mm:ss.fff} [{level}] {safeMessage}";

        try { System.Console.WriteLine(line); }
        catch { /* нет подключённой консоли — обычный запуск двойным кликом */ }

        EnsureInitialized();
        if (_initFailed) return;

        lock (Lock)
        {
            try
            {
                int byteCount = Encoding.UTF8.GetByteCount(line + System.Environment.NewLine);
                RotateIfRequired(byteCount);
                File.AppendAllText(_logFilePath, line + System.Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Проблемы самого журнала не должны ронять приложение.
            }
        }
    }

    // Сохраняет диагностическую ценность имени файла/ошибки, но скрывает имя профиля Windows
    // и сетевые UNC-пути, которые обычно не нужны при отправке лога разработчику.
    internal static string SanitizeForLog(string? value)
    {
        string result = value ?? string.Empty;
        if (result.Length > MaxMessageCharacters)
            result = result[..MaxMessageCharacters] + " … [truncated]";

        result = UserProfilePathRegex.Replace(result, "$1<user>");
        result = NetworkPathRegex.Replace(result, "<network-path>");
        return result;
    }

    private static void RotateIfRequired(int pendingBytes)
    {
        try
        {
            if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length + pendingBytes > MaxSingleLogBytes)
            {
                _currentLogPart++;
                _logFilePath = BuildLogFilePath(_currentLogPart);
                PruneOldLogs();
            }
        }
        catch
        {
            // Если размер проверить нельзя, AppendAllText ниже остаётся безопасной последней попыткой.
        }
    }

    // Открывает папку с логами в Проводнике. Создаёт её заранее, если в текущем сеансе ещё не
    // было записи — иначе Проводник не смог бы открыть несуществующий путь.
    public static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            Process.Start(new ProcessStartInfo(LogsDir) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            Error("Не удалось открыть папку с логами", ex);
        }
    }
}
