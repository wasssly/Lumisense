using System.Windows;

namespace AudioPlayer;

public partial class App : Application
{
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

        var window = new MainWindow();
        MainWindow = window;
        window.StartupPresent();
    }
}
