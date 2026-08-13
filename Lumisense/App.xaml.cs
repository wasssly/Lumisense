using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AudioPlayer;

public partial class App : Application
{
    // Global-имена не нужны — плеер не запускается из разных пользовательских сессий одновременно
    private const string SingleInstanceMutexName = "Lumisense_SingleInstance_9F3C7B21";
    private const string ToggleViewEventName = "Lumisense_ToggleView_9F3C7B21";

    // держим живыми на всё время работы приложения, иначе GC может собрать их раньше времени
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _toggleViewEvent;

    // WinExe не создаёт консоль сам — без этого Console.WriteLine никуда не пишет, даже
    // при запуске из cmd/PowerShell. Подключаемся к консоли родителя, если она есть;
    // если приложение запущено двойным кликом, AttachConsole просто вернёт false — не ошибка.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    // Окно создаём вручную вместо StartupUri="MainWindow.xaml": Show() безусловно выставляет
    // Visibility.Visible, даже если MainWindow уже спрятало себя через Hide() при восстановлении
    // мини-режима — иначе при запуске в мини-режиме на мгновение мелькало пустое главное окно.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try { AttachConsole(AttachParentProcess); } catch { /* нет родительской консоли — и ладно */ }

        // Логируем необработанные исключения максимально рано, иначе падение до показа первого
        // окна выглядело как полная тишина — раньше это шло только в консоль (Console.Error),
        // которую почти никто не видит при обычном запуске двойным кликом: окно консоли не
        // создаётся, AttachConsole выше подключается только если плеер запущен ИЗ уже открытой
        // консоли/PowerShell. Теперь то же самое ещё и пишется в файл (см. Logger) — именно
        // ради случая "плеер упал, а почему — неизвестно": после падения файл в
        // %AppData%\Lumisense\logs\ остаётся, в отличие от текста в уже закрывшейся консоли.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Необработанное исключение (AppDomain, приложение сейчас завершится)",
                args.ExceptionObject as Exception);

        // В отличие от AppDomain.UnhandledException выше (после него процесс так или иначе
        // завершается — CLR ловит это только для протоколирования, помешать выходу нельзя),
        // здесь можно предотвратить падение целиком: большинство таких исключений — это
        // необработанная ошибка в одном конкретном обработчике события UI-потока (клик по
        // кнопке, таймер и т.п.), а не повреждённое состояние всего процесса. Логируем,
        // сообщаем пользователю, что что-то пошло не так, и e.Handled = true — плеер
        // продолжает работать дальше, вместо гарантированного падения на ровном месте.
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Необработанное исключение в UI-потоке", args.Exception);

            try
            {
                System.Windows.MessageBox.Show(
                    $"Что-то пошло не так, но плеер попробует продолжить работу.\n\nПодробности сохранены в лог-файл, его можно найти в настройках (страница \"Обновления\") или в папке %AppData%\\Lumisense\\logs.\n\n{args.Exception.Message}",
                    "Lumisense — внутренняя ошибка",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            catch
            {
                // Если даже показать MessageBox не удалось (сама WPF-подсистема в нерабочем
                // состоянии) — по крайней мере в лог оно уже записано строкой выше.
            }

            args.Handled = true;
        };

        // В дополнение к двум обработчикам выше — исключения из "забытых" async-задач
        // (fire-and-forget вида "_ = SomeAsync()", которых в плеере несколько: расчёт формы
        // волны, автообновление и т.п.) сами по себе НЕ попадают ни в DispatcherUnhandledException,
        // ни в AppDomain.UnhandledException — необработанное исключение внутри такой задачи
        // просто оседает в самой Task, и всплывает только когда сборщик мусора уничтожает её
        // экземпляр, через это событие. Без него подобные ошибки были бы попросту невидимы —
        // ни падения, ни следа в логе.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Необработанное исключение в фоновой задаче (fire-and-forget)", args.Exception);
            args.SetObserved();
        };

        Logger.Info($"Lumisense запускается — версия ОС {Environment.OSVersion}, .NET {Environment.Version}, 64-бит: {Environment.Is64BitProcess}");

        // В мини-режиме окна плеера не видны на панели задач, поэтому повторный клик по ярлыку
        // запустил бы второй процесс вместо активации уже открытого. Именованный Mutex — обычный
        // приём single-instance: если он уже занят, сигналим работающему экземпляру переключить
        // вид (см. WaitForToggleSignal) и сразу выходим.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);

        if (!createdNew)
        {
            Logger.Info("Плеер уже запущен — переключаю вид у уже открытого экземпляра и завершаюсь (это не ошибка).");

            try
            {
                using var existingToggleEvent = EventWaitHandle.OpenExisting(ToggleViewEventName);
                existingToggleEvent.Set();
            }
            catch (Exception ex)
            {
                // редкая гонка: основной процесс мог начать завершаться между проверкой Mutex
                // и открытием события — тихо выходим, не показывая ошибку на пустом месте
                Logger.Warn($"Не удалось просигналить уже запущенному экземпляру: {ex.Message}");
            }

            Shutdown();
            return;
        }

        _toggleViewEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleViewEventName);

        // На случай, если конструктор MainWindow бросит исключение ещё до того, как окно
        // вообще успело появиться (например, повреждённый settings.json провоцирует where-то
        // внутри необработанное исключение, до которого не добрались более точечные try/catch
        // внутри самого MainWindow) — тут это ловится максимально широко: логируем, показываем
        // сообщение вместо тихого падения без единого следа, и корректно завершаемся, а не
        // остаёмся в неопределённом полуживом состоянии.
        MainWindow window;
        try
        {
            window = new MainWindow();
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось создать главное окно — плеер не может запуститься", ex);

            try
            {
                System.Windows.MessageBox.Show(
                    $"Lumisense не удалось запуститься.\n\nПодробности сохранены в лог-файл (%AppData%\\Lumisense\\logs).\n\n{ex.Message}",
                    "Lumisense — ошибка запуска",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch { /* см. аналогичный catch в DispatcherUnhandledException выше */ }

            Shutdown();
            return;
        }

        MainWindow = window;
        window.StartupPresent();

        Logger.Info("Главное окно создано и показано — запуск завершён успешно.");

        WaitForToggleSignal(window);
    }

    // Ждёт сигнала от повторного запуска в фоновом потоке и переключает вид через Dispatcher.Invoke.
    // IsBackground=true — поток не должен сам по себе держать процесс живым
    private void WaitForToggleSignal(MainWindow window)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                _toggleViewEvent!.WaitOne();
                Dispatcher.Invoke(window.ToggleMiniOrMainFromExternalActivation);
            }
        })
        {
            IsBackground = true,
            Name = "Lumisense.ToggleViewListener"
        };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info($"Lumisense завершается (код выхода {e.ApplicationExitCode})");

        // Явный ReleaseMutex — иначе следующий запуск ещё некоторое время видит Mutex занятым,
        // хотя окно уже закрыто (он живёт, пока процесс не завершит ОС)
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
