using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumisense;

// Локальный файловый журнал: по сети не уходит, но может попасть в issue — персональные
// сегменты путей вырезаются. Лимиты размера защищают %AppData% от разрастания.
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

    // Размер текущего файла в памяти: иначе каждая строка журнала стоила бы лишних File.Exists и FileInfo.
    // -1 — ещё не измерен (первая запись сеанса).
    private static long _currentLogBytes = -1;

    // Строки копятся в памяти и пишутся на диск пачкой: раньше каждая стоила отдельного открытия файла (File.AppendAllText).
    // Ошибки и выход из приложения сбрасывают очередь сразу, остальное уходит на диск не позже чем через FlushDelayMilliseconds.
    // При аварийном завершении без ProcessExit (fail-fast) теряются только последние INFO/WARN этого окна.
    private const int FlushDelayMilliseconds = 400;
    private const int MaxPendingLines = 500;
    private static readonly List<string> Pending = new();
    private static System.Threading.Timer? _flushTimer;
    private static bool _flushScheduled;

    // Подряд идущие одинаковые сообщения (цикл, который каждый тик падает с одной и той же ошибкой) схлопываются в одну строку
    // и пометку «повторилось ещё N раз». Сообщение считается повтором, если с предыдущего такого же прошло меньше RepeatWindowMilliseconds.
    private const long RepeatWindowMilliseconds = 60_000;
    private static string? _lastMessageKey;
    private static long _lastMessageTick;
    private static int _repeatCount;

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
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
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
            string key = level + "|" + safeMessage;
            long now = System.Environment.TickCount64;
            if (key == _lastMessageKey && now - _lastMessageTick < RepeatWindowMilliseconds)
            {
                _repeatCount++;
                _lastMessageTick = now;
                return;
            }

            AddRepeatNoteLocked();
            _lastMessageKey = key;
            _lastMessageTick = now;

            Pending.Add(line);
            if (level == "ERROR" || Pending.Count >= MaxPendingLines)
            {
                FlushLocked();
                return;
            }

            if (_flushScheduled) return;

            _flushScheduled = true;
            try
            {
                _flushTimer ??= new System.Threading.Timer(_ => FlushPending(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _flushTimer.Change(FlushDelayMilliseconds, System.Threading.Timeout.Infinite);
            }
            catch
            {
                // Без таймера строки всё равно не потеряются: их заберёт следующий сброс.
                _flushScheduled = false;
                FlushLocked();
            }
        }
    }

    // Записывает накопленные строки на диск; безопасно вызывать из любого потока и сколько угодно раз.
    public static void Flush()
    {
        lock (Lock)
        {
            // Явный сброс (выход, открытие папки журналов) закрывает серию повторов, чтобы пометка не потерялась.
            AddRepeatNoteLocked();
            _lastMessageKey = null;
            FlushLocked();
        }
    }

    private static void AddRepeatNoteLocked()
    {
        if (_repeatCount <= 0) return;

        Pending.Add($"{System.DateTime.Now:HH:mm:ss.fff} [INFO] Предыдущее сообщение повторилось ещё {_repeatCount} раз(а), повторы не записываются.");
        _repeatCount = 0;
    }

    // Сброс по таймеру: серию повторов не закрывает, иначе окно схлопывания не превышало бы задержки таймера.
    private static void FlushPending()
    {
        lock (Lock) FlushLocked();
    }

    private static void FlushLocked()
    {
        _flushScheduled = false;
        if (Pending.Count == 0) return;

        try
        {
            var chunk = new StringBuilder();
            foreach (string line in Pending)
            {
                string text = line + System.Environment.NewLine;
                int bytes = Encoding.UTF8.GetByteCount(text);
                // _currentLogBytes уже включает строки ещё не записанной пачки: при переполнении она уходит в старый файл,
                // а текущая строка начинает новый.
                if (NeedsRotation(bytes))
                {
                    AppendChunk(chunk);
                    Rotate();
                }

                chunk.Append(text);
                _currentLogBytes += bytes;
            }

            AppendChunk(chunk);
        }
        catch
        {
            // Проблемы самого журнала не должны ронять приложение.
        }
        finally
        {
            Pending.Clear();
        }
    }

    private static void AppendChunk(StringBuilder chunk)
    {
        if (chunk.Length == 0) return;

        try
        {
            File.AppendAllText(_logFilePath, chunk.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Занятый или недоступный файл: пачка теряется, как раньше терялась бы одна строка.
        }

        chunk.Clear();
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

    private static bool NeedsRotation(int pendingBytes)
    {
        try
        {
            if (_currentLogBytes < 0)
                _currentLogBytes = File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0;

            return _currentLogBytes > 0 && _currentLogBytes + pendingBytes > MaxSingleLogBytes;
        }
        catch
        {
            // Если размер проверить нельзя, AppendAllText остаётся безопасной последней попыткой.
            return false;
        }
    }

    private static void Rotate()
    {
        try
        {
            _currentLogPart++;
            _logFilePath = BuildLogFilePath(_currentLogPart);
            _currentLogBytes = 0;
            PruneOldLogs();
        }
        catch
        {
            // Следующая строка попробует ещё раз.
        }
    }

    // Открывает папку с логами в Проводнике. Создаёт её заранее, если в текущем сеансе ещё не
    // было записи — иначе Проводник не смог бы открыть несуществующий путь.
    public static void OpenLogsFolder()
    {
        try
        {
            Flush();
            Directory.CreateDirectory(LogsDir);
            Process.Start(new ProcessStartInfo(LogsDir) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            Error("Не удалось открыть папку с логами", ex);
        }
    }
}
