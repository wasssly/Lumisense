using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Lumisense;

public partial class App : Application
{
    // Global-имена не нужны — плеер не запускается из разных пользовательских сессий одновременно
    private const string SingleInstanceMutexName = "Lumisense_SingleInstance_9F3C7B21";
    private const string ToggleViewEventName = "Lumisense_ToggleView_9F3C7B21";

    // держим живыми на всё время работы приложения, иначе GC может собрать их раньше времени
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _toggleViewEvent;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _isOrderlyExit;
    private int _lastChanceSettingsSaveStarted;

    // WinExe не создаёт консоль: подключаемся к консоли родителя (cmd/PowerShell), чтобы Console.WriteLine работал;
    // при двойном клике AttachConsole просто вернёт false.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    // Окно создаём вручную вместо StartupUri: Show() безусловно ставит Visibility.Visible, даже если MainWindow
    // спрятало себя через Hide() при старте в мини-режиме — иначе мелькало бы пустое главное окно.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Все окна получают текущий язык после построения визуального дерева — без копирования вызова во все конструкторы.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                LocalizationService.Apply(sender);

                // Часть подписей создаётся в Loaded конкретных окон: повторный проход на ContextIdle идёт после этих
                // обработчиков и до первого устойчивого кадра, поэтому английский текст не остаётся русским.
                if (sender is Window window)
                {
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (window.IsLoaded)
                            LocalizationService.Apply(window);
                    }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
            }));

        // ContextMenu и ToolTip находятся в отдельных Popup-деревьях и часть из них создаётся
        // только после Loaded окна. Переводим их непосредственно перед отображением.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu),
            System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => LocalizationService.Apply(sender)));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip),
            System.Windows.Controls.ToolTip.OpenedEvent,
            new RoutedEventHandler((sender, _) => LocalizationService.Apply(sender)));

        try { AttachConsole(AttachParentProcess); } catch { /* нет родительской консоли — и ладно */ }

        // Ctrl+C и закрытие консольного сеанса могут оборвать обычный WPF shutdown: обработчики — best-effort
        // дополнение к односекундному checkpoint MainWindow, синхронно пишут готовый snapshot без обращения к UI.
        Console.CancelKeyPress += (_, _) => SaveSettingsOnUnexpectedTermination();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveSettingsOnUnexpectedTermination();

        // Логируем необработанные исключения как можно раньше в файл (Logger): при запуске двойным кликом консоли нет,
        // а лог в %AppData%\Lumisense\logs\ остаётся после падения, в отличие от текста закрывшейся консоли.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Error("Необработанное исключение (AppDomain, приложение сейчас завершится)",
                args.ExceptionObject as Exception);
            SaveSettingsOnUnexpectedTermination();
        };

        // DispatcherUnhandledException можно подавить, но для любой ошибки это небезопасно: неизвестное исключение
        // может повредить аудио-цепочку или визуальное дерево. Продолжаем только для ожидаемых локальных ошибок.
        DispatcherUnhandledException += (_, args) =>
        {
            UiExceptionRecoveryAction action = UiExceptionRecoveryPolicy.Classify(args.Exception);
            Logger.Error($"Необработанное исключение в UI-потоке; действие: {action}", args.Exception);

            if (action == UiExceptionRecoveryAction.Ignore)
            {
                args.Handled = true;
                return;
            }

            try
            {
                if (action == UiExceptionRecoveryAction.Continue)
                {
                    LocalizedMessageBox.Show(
                        $"Что-то пошло не так, но плеер попробует продолжить работу.\n\nПодробности сохранены в лог-файл, его можно найти в настройках (страница \"Обновления\") или в папке %AppData%\\Lumisense\\logs.\n\n{args.Exception.Message}",
                        "Lumisense — внутренняя ошибка",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    args.Handled = true;
                    return;
                }

                LocalizedMessageBox.Show(
                    $"В Lumisense произошла непредвиденная ошибка. Чтобы защитить данные и состояние воспроизведения, приложение будет закрыто.\n\nПодробности сохранены в лог-файл, его можно найти в папке %AppData%\\Lumisense\\logs.\n\n{args.Exception.Message}",
                    "Lumisense — критическая ошибка",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch
            {
                // Если даже показать сообщение не удалось, WPF всё равно завершит приложение:
                // args.Handled намеренно остаётся false для неизвестного критического состояния.
            }
        };

        // Исключения из fire-and-forget задач ("_ = SomeAsync()") не попадают в предыдущие два обработчика и всплывают
        // лишь при финализации Task; без этого события такие ошибки были бы невидимы (ни падения, ни следа в логе).
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Необработанное исключение в фоновой задаче (fire-and-forget)", args.Exception);
            args.SetObserved();
        };

        Logger.Info($"Lumisense запускается — версия ОС {Environment.OSVersion}, .NET {Environment.Version}, 64-бит: {Environment.Is64BitProcess}");
        // Наблюдаемый migration guard: legacy Inno Setup остаётся основным путём, пока
        // приложение не будет установлено через Velopack MSI в отдельном переходном релизе.
        UpdateMigrationGuard.LogCurrentMode();

        // В мини-режиме окон нет на панели задач, и повторный клик по ярлыку запустил бы второй процесс: если Mutex
        // занят, сигналим работающему экземпляру переключить вид (см. WaitForToggleSignal) и выходим.
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

        // Конструктор MainWindow может бросить исключение до появления окна (например, из-за повреждённого settings.json):
        // ловим широко, логируем и показываем сообщение вместо тихого падения, затем корректно завершаемся.
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
                LocalizedMessageBox.Show(
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

        // Дешёвая проверка реестра, без влияния на время запуска (см. RepairContextMenuScopeIfBroken).
        Task.Run(LegacyIntegrationRepairService.RepairContextMenuScopeIfBroken);
        // Отдельный, гораздо более редкий случай (см. метод) — свой Task.Run, чтобы UAC-промпт
        // (если он вообще понадобится) не блокировал очередь выше.
        Task.Run(LegacyIntegrationRepairService.TryCleanupLegacyHklmWildcardContextMenu);

        // Этот вызов возможен лишь после успешного создания MSI-окна. Он одноразово обрабатывает
        // marker Velopack и только при точном обнаружении legacy Inno Setup предлагает cleanup.
        UpdateMigrationGuard.TryShowFirstRunNotice();

        WaitForToggleSignal(window);
    }

    // Ждёт сигнала от повторного запуска в фоновом потоке и переключает вид через Dispatcher.Invoke.
    // IsBackground=true — поток не должен сам по себе держать процесс живым
    private void WaitForToggleSignal(MainWindow window)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (!_shutdownCts.IsCancellationRequested)
                {
                    if (!_toggleViewEvent!.WaitOne(500)) continue;
                    if (_shutdownCts.IsCancellationRequested || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                        break;
                    Dispatcher.Invoke(() =>
                    {
                        if (!_shutdownCts.IsCancellationRequested && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                            window.ToggleMiniOrMainFromExternalActivation();
                    });
                }
            }
            catch (AbandonedMutexException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) when (_shutdownCts.IsCancellationRequested) { }
        })
        {
            IsBackground = true,
            Name = "Lumisense.ToggleViewListener"
        };
        thread.Start();
    }

    // Вызывается из UI-потока после SettingsManager.Save и перед передачей управления Update.exe: ProcessExit этого
    // restart не должен повторно писать snapshot с фона (убирает warning, аварийное сохранение не ослабляется).
    internal void MarkPlannedUpdateRestart()
    {
        Volatile.Write(ref _isOrderlyExit, 1);
    }

    // Если сам запуск Update.exe выбросил исключение и приложение остаётся открытым, возвращаем
    // аварийный путь сохранения для последующих действительно непредвиденных завершений.
    internal void CancelPlannedUpdateRestart()
    {
        Volatile.Write(ref _isOrderlyExit, 0);
    }

    private void SaveSettingsOnUnexpectedTermination()
    {
        if (Volatile.Read(ref _isOrderlyExit) != 0
            || Interlocked.CompareExchange(ref _lastChanceSettingsSaveStarted, 1, 0) != 0)
            return;

        try
        {
            // ProcessExit и CancelKeyPress могут идти не в Dispatcher-потоке: не трогаем MainWindow, берём последний
            // атомарно опубликованный JSON, чтобы не читать WPF UI-объекты при разрушении приложения.
            SettingsManager.SaveLastObservedSnapshot();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось выполнить last-chance сохранение настроек: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Volatile.Write(ref _isOrderlyExit, 1);
        Logger.Info($"Lumisense завершается (код выхода {e.ApplicationExitCode})");

        _shutdownCts.Cancel();
        try { _toggleViewEvent?.Set(); } catch (ObjectDisposedException) { }
        _toggleViewEvent?.Dispose();
        _toggleViewEvent = null;

        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _shutdownCts.Dispose();
        base.OnExit(e);
    }
}
