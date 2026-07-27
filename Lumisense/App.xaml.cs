using System.Runtime.InteropServices;
using System.Threading;
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
        // окна выглядело как полная тишина в консоли
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Console.Error.WriteLine($"[Lumisense] Необработанное исключение: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
            Console.Error.WriteLine($"[Lumisense] Необработанное исключение в UI-потоке: {args.Exception}");

        // В мини-режиме окна плеера не видны на панели задач, поэтому повторный клик по ярлыку
        // запустил бы второй процесс вместо активации уже открытого. Именованный Mutex — обычный
        // приём single-instance: если он уже занят, сигналим работающему экземпляру переключить
        // вид (см. WaitForToggleSignal) и сразу выходим.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);

        if (!createdNew)
        {
            Console.WriteLine("[Lumisense] Плеер уже запущен — переключаю вид у уже открытого экземпляра и завершаюсь (это не ошибка).");

            try
            {
                using var existingToggleEvent = EventWaitHandle.OpenExisting(ToggleViewEventName);
                existingToggleEvent.Set();
            }
            catch (Exception ex)
            {
                // редкая гонка: основной процесс мог начать завершаться между проверкой Mutex
                // и открытием события — тихо выходим, не показывая ошибку на пустом месте
                Console.Error.WriteLine($"[Lumisense] Не удалось просигналить уже запущенному экземпляру: {ex.Message}");
            }

            Shutdown();
            return;
        }

        _toggleViewEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleViewEventName);

        var window = new MainWindow();
        MainWindow = window;
        window.StartupPresent();

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
        // Явный ReleaseMutex — иначе следующий запуск ещё некоторое время видит Mutex занятым,
        // хотя окно уже закрыто (он живёт, пока процесс не завершит ОС)
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
