using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Updater;

// Отдельный процесс, который довершает обновление Lumisense через ZIP уже после того, как
// сам плеер скачал и распаковал новую версию во временную папку и завершил себя (см.
// UpdateChecker.LaunchUpdaterAndExit в основном проекте — оттуда Updater и запускается).
//
// Плеер сам себя не перезаписывает намеренно: пока Lumisense.exe работает, его собственные
// файлы (сам .exe, DLL и т.д.) заняты и заменить их на месте нельзя, а после Shutdown() у
// работающего процесса уже нет возможности что-либо доделать. Поэтому финальный шаг —
// дождаться завершения плеера, скопировать новые файлы поверх старых, запустить новую версию
// и убрать за собой — вынесен в этот отдельный, маленький и независимый экзешник.
//
// Ожидаемые аргументы (все обязательны, передаются позиционно — см. LaunchUpdaterAndExit):
//   args[0] — PID процесса Lumisense.exe, который нужно дождаться перед заменой файлов
//   args[1] — путь к папке с новой версией (распакованный ZIP, источник копирования)
//   args[2] — путь к папке установки (куда копировать, {app} — она же папка с работающим
//             сейчас Lumisense.exe)
//   args[3] — имя exe-файла плеера для повторного запуска после обновления (Lumisense.exe)
//   args[4] — папка временных файлов скачивания/распаковки, которую нужно удалить после
//             копирования (НЕ совпадает с папкой, откуда исполняется сам Updater.exe — см.
//             ScheduleSelfCleanup ниже)
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            if (args.Length < 5)
                throw new ArgumentException("Updater запущен с недостаточным количеством параметров.");

            int playerPid = ParsePid(args[0]);
            string sourceDir = args[1];
            string targetDir = args[2];
            string exeName = args[3];
            string downloadCleanupDir = args[4];

            WaitForPlayerExit(playerPid);

            if (!Directory.Exists(sourceDir) || Directory.GetFileSystemEntries(sourceDir).Length == 0)
                throw new DirectoryNotFoundException($"Папка с файлами обновления пуста или не найдена: {sourceDir}");

            Directory.CreateDirectory(targetDir);
            CopyDirectoryOverwrite(sourceDir, targetDir);

            string targetExePath = Path.Combine(targetDir, exeName);
            if (!File.Exists(targetExePath))
                throw new FileNotFoundException($"После копирования не найден исполняемый файл плеера: {targetExePath}");

            LaunchUpdatedPlayer(targetExePath);

            TryDeleteDirectory(downloadCleanupDir);
            ScheduleSelfCleanup();
        }
        catch (Exception ex)
        {
            // Updater работает без окна, поэтому единственный способ сообщить пользователю,
            // что обновление не завершилось — модальный MessageBox. Тихо промолчать нельзя:
            // человек в этот момент уже видит закрывшийся плеер и ждёт, что тот вот-вот
            // перезапустится сам.
            MessageBox.Show(
                $"Не удалось завершить обновление Lumisense.\n\n{ex.Message}\n\n" +
                "Попробуйте запустить Lumisense ещё раз — если он не запускается, скачайте " +
                "актуальную версию с страницы релизов на GitHub и переустановите программу.",
                "Lumisense — обновление",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static int ParsePid(string raw)
    {
        if (!int.TryParse(raw, out int pid))
            throw new ArgumentException($"Некорректный PID процесса плеера: \"{raw}\".");
        return pid;
    }

    // Плеер завершает себя вызовом Application.Current.Shutdown() сразу после запуска
    // Updater — но сам процесс закрывается не мгновенно (диспетчер WPF, освобождение
    // аудио-потоков NAudio, сохранение настроек и т.п.), поэтому ждём реального завершения
    // процесса по PID, а не полагаемся на то, что раз Updater уже стартовал — плеер точно
    // выгружен и не держит файлы.
    private static void WaitForPlayerExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Процесса с таким PID уже нет — значит, плеер уже успел закрыться, всё в порядке
        }

        // Небольшой запас: файловые хендлы освобождаются не всегда в тот же момент, что и сам
        // процесс — особенно у self-contained single-file сборок.
        Thread.Sleep(500);
    }

    // Копирует sourceDir поверх targetDir рекурсивно, заменяя существующие файлы — так
    // обновляются и .exe, и все .dll, и ресурсы, темы, локализации, что угодно ещё, что
    // окажется в папке новой версии, без привязки к конкретному списку файлов. Файлы, которых
    // в новой версии больше нет, в targetDir намеренно не удаляются — это, например, могут
    // быть пользовательские данные или файлы сторонних программ, оставленные в той же папке;
    // Updater отвечает только за замену того, что реально пришло в обновлении.
    private static void CopyDirectoryOverwrite(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string destPath = Path.Combine(targetDir, relative);
            CopyFileWithRetry(file, destPath);
        }
    }

    // Несколько попыток с паузой: сразу после завершения процесса плеера файл иногда ещё на
    // мгновение занят (антивирус, индексатор Windows, не до конца отпущенный хендл) — обычная
    // ситуация для обновляторов, решается коротким ретраем, а не падением всего обновления.
    private static void CopyFileWithRetry(string sourceFile, string destFile)
    {
        const int maxAttempts = 6;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(sourceFile, destFile, overwrite: true);
                return;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                Thread.Sleep(300);
            }
        }
    }

    // Updater к этому моменту запущен с повышенными правами (см. app.manifest —
    // requireAdministrator, нужно было для записи в Program Files). Если запустить плеер
    // отсюда напрямую, он унаследует повышенный токен и будет работать от имени
    // администратора — нежелательно для обычного плеера, который в этом не нуждается. Чтобы
    // "де-элевировать" новый процесс, просим его открыть explorer.exe: сам Explorer уже
    // работает с обычными правами пользователя, и запущенный через него процесс получает его
    // уровень прав, а не прав Updater'а. Это стандартный, широко используемый приём для именно
    // такого сценария (элевированный установщик/обновлятор запускает обычное приложение).
    private static void LaunchUpdatedPlayer(string targetExePath)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{targetExePath}\"")
        {
            UseShellExecute = true
        });
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Временные файлы скачивания/распаковки — не критично, если не удалились с первой
            // попытки, это не мешает завершившемуся обновлению
        }
    }

    // Собственную папку (там, откуда прямо сейчас выполняется этот Updater.exe) нельзя
    // удалить, пока процесс ещё жив — Windows держит файл образа открытым на исполнение.
    // Поручаем удаление отдельному отсоединённому процессу с небольшой задержкой: за пару
    // секунд Updater успевает выйти и освободить хендл, а его временная папка (несколько
    // мегабайт в %TEMP%) корректно убирается следом. Результат этой команды не проверяется и
    // не докладывается пользователю — задача просто "убрать за собой", а не отчитаться.
    private static void ScheduleSelfCleanup()
    {
        try
        {
            string ownDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(ownDir) || !Directory.Exists(ownDir)) return;

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{ownDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            // Не критично — это всего пара мегабайт в %TEMP%, система и так периодически
            // подчищает эту папку самостоятельно
        }
    }
}
