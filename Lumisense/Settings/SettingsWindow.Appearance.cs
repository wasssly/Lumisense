using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static Lumisense.BackgroundTask;

namespace Lumisense;

public partial class SettingsWindow
{
    // WPF-UI 4 ставит собственное обновление FluentWindow в очередь Dispatcher после ApplicationThemeManager.Apply и может
    // перезаписать backdrop дефолтным Mica, поэтому повторяем выбранный backdrop на ContextIdle (AppSettings не меняем).
    internal void ReapplyWindowBackdropsAfterThemeChange()
    {
        _owner.ApplyWindowBackdrop(forceReapply: true);
        ApplyWindowBackdrop(_settings, forceReapply: true);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _owner.ApplyWindowBackdrop(forceReapply: true);
            if (IsLoaded)
                ApplyWindowBackdrop(_settings, forceReapply: true);
        }), DispatcherPriority.ContextIdle);
    }

    // Страница «Оформление» меняет то, что принадлежит самому окну: вид навигации и подложку.
    internal void ApplyNavigationStyleFromSettings() => ApplyNavigationStyle(_settings.SettingsNavigationStyle);

    internal void ApplyOwnBackdrop() => ApplyWindowBackdrop(_settings);

    // Обёртка над HWND для System.Windows.Forms.IWin32Window — ColorDialog просит именно
    // этот интерфейс в качестве owner, а не голый IntPtr.
    internal sealed class Wpf32Window : System.Windows.Forms.IWin32Window
    {
        public Wpf32Window(nint handle) => Handle = handle;
        public nint Handle { get; }
    }

}
