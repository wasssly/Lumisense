using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AudioPlayer;

// Простой файловый логгер — своя реализация, а не внешняя библиотека (Serilog и т.п.): всё,
// что реально нужно — "если плеер упал или повёл себя странно, куда посмотреть, что случилось",
// а не гибкая система с синками/шаблонами. Пишет в %AppData%\Lumisense\logs\, рядом с тем же
// местом, где лежит settings.json (см. SettingsManager).
//
// Каждый запуск — отдельный файл (а не один общий, вечно дописываемый) — так сразу видно
// границы конкретной сессии, не нужно вручную выискивать в общем файле, где закончился
// предыдущий запуск и начался этот. См. OpenLogsFolder — кнопка в настройках открывает эту же
// папку в Проводнике, чтобы файлы было легко найти и приложить к сообщению об ошибке.
public static class Logger
{
    private static readonly string LogsDir = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Lumisense", "logs");

    private static readonly string LogFilePath = Path.Combine(
        LogsDir, $"lumisense_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

    // Пишем строго последовательно — записи могут прилететь одновременно из разных потоков
    // (UI, фоновые задачи, обработчики необработанных исключений в App.xaml.cs), без лока
    // строки в файле могли бы перемежаться и ломать читаемость.
    private static readonly object Lock = new();

    private static bool _initialized;
    private static bool _initFailed;

    // Держим только последние MaxLogFiles файлов — иначе за месяцы использования папка logs
    // накопила бы сотни файлов. Каждый файл сам по себе небольшой (текстовые строки за одну
    // сессию), поэтому ограничение по количеству, а не по суммарному размеру, вполне достаточно.
    private const int MaxLogFiles = 50;

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
            // Нет прав на запись, диск заполнен и т.п. — само логирование не должно ронять
            // приложение или мешать ему работать; просто перестаём пытаться писать в файл
            // (Info/Warn/Error всё ещё дублируют в консоль, см. Write ниже).
            _initFailed = true;
        }
    }

    private static void PruneOldLogs()
    {
        var oldFiles = new DirectoryInfo(LogsDir).GetFiles("lumisense_*.log")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(MaxLogFiles);

        foreach (var file in oldFiles)
        {
            try { file.Delete(); }
            catch { /* не критично — попробуем удалить в следующий раз */ }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, System.Exception? ex = null)
    {
        Write("ERROR", ex == null ? message : $"{message}: {ex}");
    }

    private static void Write(string level, string message)
    {
        string line = $"{System.DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        // Дублируем в консоль — тот же смысл, который раньше был у прямых Console.WriteLine
        // в App.xaml.cs: для тех, кто запускает плеер из консоли/PowerShell и смотрит туда
        // напрямую. Файл — не замена этому, а дополнение на случай "уже закрылось, а я не видел".
        try { Console.WriteLine(line); }
        catch { /* нет подключённой консоли (обычный запуск двойным кликом) — и не нужно */ }

        EnsureInitialized();
        if (_initFailed) return;

        lock (Lock)
        {
            try
            {
                File.AppendAllText(LogFilePath, line + System.Environment.NewLine);
            }
            catch
            {
                // См. EnsureInitialized выше — не роняем приложение из-за проблем с самим логом.
            }
        }
    }

    // Открывает папку с логами в Проводнике (см. кнопку в настройках, страница "Обновления").
    // Создаёт папку заранее, если в ней ещё ни разу ничего не залогировали — иначе Проводник
    // просто не откроется на несуществующем пути.
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
