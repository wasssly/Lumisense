using System.Threading;
using System.Windows;

namespace AudioPlayer;

public partial class App : Application
{
    // Общие на все процессы (Global-имена нужны, только если плеер может запускаться из-под
    // разных пользовательских сессий одновременно — здесь это не так, обычных имён достаточно)
    // имена Mutex/EventWaitHandle для однократного запуска: по ним второй процесс узнаёт, что
    // плеер уже работает, а не проверяет, скажем, список запущенных процессов по имени (это
    // ненадёжно — под тем же именем может работать и вообще не Lumisense).
    private const string SingleInstanceMutexName = "Lumisense_SingleInstance_9F3C7B21";
    private const string ToggleViewEventName = "Lumisense_ToggleView_9F3C7B21";

    // Держим Mutex и поток ожидания сигналов живыми на всё время работы приложения — если
    // просто создать их как локальные переменные внутри OnStartup, GC вполне может собрать их
    // (а с Mutex — ещё и освободить) сразу после выхода из метода, а не когда реально закроется
    // само приложение.
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _toggleViewEvent;

    // Раньше показ главного окна был отдан на откуп StartupUri="MainWindow.xaml" — WPF сам
    // создавал MainWindow и сразу вызывал Show(). Проблема в том, что Show() безусловно
    // выставляет Visibility.Visible, даже если код внутри окна (например, восстановление
    // мини-режима) до этого явно спрятал окно через Hide() — Show() эту попытку спрятать
    // просто перезаписывает. Из-за этого при запуске в мини-режиме пользователь на мгновение
    // видел пустое главное окно поверх/рядом с мини-плеером.
    //
    // Поэтому здесь окно создаётся вручную, и Show() на нём вызывается только если решено,
    // что стартовый вид — НЕ мини-режим (см. MainWindow.StartupPresent). Если же последним
    // был мини-режим, окно вообще ни разу не показывается — Show() на нём просто не
    // вызывается, и никакой вспышки не возникает в принципе.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // В мини-режиме у окон плеера нет присутствия на панели задач вообще (ни у главного
        // окна — оно спрятано через Hide, ни у самого мини-плеера — у него ShowInTaskbar=False,
        // см. MiniPlayerWindow.xaml), поэтому повторный клик по ярлыку плеера на панели задач/в
        // меню Пуск в этом состоянии не активирует уже запущенное окно (Windows попросту не
        // знает, с каким окном связать этот ярлык), а запускает НОВЫЙ процесс плеера — и без
        // проверки ниже пользователь получил бы два одновременно запущенных плеера. Именованный
        // Mutex — стандартный приём single-instance: если он уже существует, значит плеер уже
        // запущен, и вместо создания второго окна просто сигналим уже работающему экземпляру
        // переключиться между обычным окном и мини-плеером (см. WaitForToggleSignal ниже) и
        // сразу завершаемся сами, ничего не создавая.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);

        if (!createdNew)
        {
            try
            {
                using var existingToggleEvent = EventWaitHandle.OpenExisting(ToggleViewEventName);
                existingToggleEvent.Set();
            }
            catch
            {
                // Основной процесс мог оказаться в процессе завершения ровно между проверкой
                // Mutex выше и открытием события — крайне маловероятная гонка, но в этом случае
                // просто тихо выходим, не переключая ничего и не показывая ошибку на пустом месте.
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

    // Фоновый поток ждёт сигнала от повторного запуска (см. выше) и переключает вид плеера
    // через Dispatcher.Invoke — сам EventWaitHandle.WaitOne блокирует поток из пула потоков,
    // а не UI-поток, поэтому не мешает работе интерфейса, пока сигнала нет. IsBackground=true —
    // чтобы этот поток сам по себе не держал процесс живым дольше, чем нужно, если что-то
    // пойдёт не так с обычным путём завершения через OnExit.
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
        // ReleaseMutex, а не просто Dispose/выход из процесса — если этого не сделать явно,
        // до фактического освобождения Mutex у следующего запуска короткое время может
        // складываться впечатление, что плеер всё ещё работает (Mutex формально жив, пока
        // процесс не завершится ОС), хотя окно уже закрыто.
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
