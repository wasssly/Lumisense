using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using WpfSlider = System.Windows.Controls.Slider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lumisense;

// Режимы вида окна (квадратный/прямоугольный/мини) и меню выбора вида. Вынесено из
// MainWindow.xaml.cs только ради навигации, логика и порядок вызовов не менялись.
public partial class MainWindow
{
    private PlayerViewMode ResolveStartupViewMode(out bool? legacyPlaylistVisible)
    {
        PlayerViewMode startupMode;
        legacyPlaylistVisible = null;

        if (_settings.PlayerViewMode == nameof(PlayerViewMode.Square))
            startupMode = PlayerViewMode.Square;
        else if (_settings.PlayerViewMode == nameof(PlayerViewMode.Rectangular))
            startupMode = PlayerViewMode.Rectangular;
        else if (_settings.PlayerViewMode == nameof(PlayerViewMode.Mini))
            startupMode = PlayerViewMode.Mini;
        else if (_isFirstLaunch)
        {
            // Вид плеера ещё ни разу не сохранялся, и настроек вообще никогда не было —
            // самый первый запуск: открываем обычный (квадратный) вид.
            startupMode = PlayerViewMode.Square;
        }
        else
        {
            // settings.json уже есть, но вид не сохранялся — версия до появления этой настройки: открываем квадратный вид, как
            // при первом запуске; видимость плейлиста восстанавливается отдельно, поэтому у существующих пользователей не меняется.
            startupMode = _settings.WasMiniPlayerOnClose ? PlayerViewMode.Mini : PlayerViewMode.Square;
            legacyPlaylistVisible = _settings.IsPlaylistVisible;
        }

        return startupMode;
    }

    // Восстанавливает вид, в котором плеер был закрыт: скрытую панель плейлиста и/или мини-режим. Вызывается из StartupPresent
    // до Show(): при стартовом мини-режиме окно ни разу не появляется на экране.
    private void RestorePlayerViewMode(PlayerViewMode startupMode, bool? legacyPlaylistVisible)
    {
        if (startupMode == PlayerViewMode.Mini)
        {
            // Сначала приводим скрытое окно к прямоугольному виду (старая версия не различала квадратный/прямоугольный), затем
            // сворачиваем в мини-режим: EnterMiniMode запоминает верный _preMiniViewMode, и "развернуть" возвращает прямоугольный вид.
            SetPlayerViewMode(PlayerViewMode.Rectangular, persist: false);
            if (legacyPlaylistVisible == false) SetPlaylistVisibility(false);
            SetPlayerViewMode(PlayerViewMode.Mini, persist: false);
        }
        else
        {
            SetPlayerViewMode(startupMode, persist: false);
            if (legacyPlaylistVisible == false) SetPlaylistVisibility(false);
        }
    }

    // Единая точка переключения видов (квадратный/прямоугольный/мини): из меню заголовка (TitleClickArea), шеврона плейлиста
    // и при восстановлении вида на старте (RestorePlayerViewMode).
    private void SetPlayerViewMode(PlayerViewMode mode, bool persist = true)
    {
        if (mode == PlayerViewMode.Mini)
        {
            // EnterMiniMode читает _viewMode (старое значение), чтобы запомнить его в _preMiniViewMode, поэтому новое присваиваем после вызова.
            if (!_isMiniMode) EnterMiniMode();
            _viewMode = mode;
        }
        else
        {
            if (_isMiniMode) ExitMiniMode();

            _viewMode = mode;

            bool square = mode == PlayerViewMode.Square;

            // Порядок важен: сначала крупный/обычный стиль, потом высота под плейлист — SetPlaylistVisibility замеряет
            // нужную высоту уже после увеличения контента. Плейлист по умолчанию остаётся открытым и в квадратном виде.
            ApplyContentScale(square || _isFullscreenLayout);
            // Высота, запомненная при сворачивании плейлиста, относится к прежнему виду: иначе квадрат (860) перейдёт в прямоугольный.
            _heightBeforeHidingPlaylist = 0;
            SetPlaylistVisibility(true);

            if (square)
            {
                // Крупному контенту квадратного вида нужен запас сверх MinHeightWithPlaylist (680), иначе плейлист сожмётся почти в ноль;
                // на маленьких экранах не даём окну выйти за рабочую область — квадрат чуть меньше, но полностью виден.
                if (Height < SquareMinHeightWithPlaylist)
                {
                    double screenLimit = SystemParameters.WorkArea.Height - 40;
                    double targetHeight = Math.Min(SquareMinHeightWithPlaylist, Math.Max(MinHeightWithPlaylist, screenLimit));
                    Height = targetHeight;
                    MinHeight = targetHeight;
                }
                MakeWindowSquare();
            }
            else
            {
                RestoreRectangularWidth();
            }

            // MakeWindowSquare/RestoreRectangularWidth растят окно вправо-вниз от угла и могли вытолкнуть его за экран: обычный клэмп
            // в границы, без магнитного прилипания.
            ClampWindowToWorkArea();

            // Ширину контента считаем после приведения Width/Height к новому виду, иначе для квадрата использовалась бы старая
            // ширина окна и контент остался бы узкой колонкой с пустыми полями.
            UpdateContentMaxWidth();
        }

        if (persist)
        {
            _settings.PlayerViewMode = mode.ToString();
            FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
        }

        UpdateViewModeMenuChecks();
        _settingsWindow?.RefreshViewModeRadios();
    }

    // Стиль уже крупный, плейлист виден, Height подогнана под него (SquareMinHeightWithPlaylist, см. SetPlayerViewMode):
    // Width делаем равной Height, чтобы получить настоящий квадрат.
    private void MakeWindowSquare()
    {
        double size = Math.Max(Height, MinWidth);
        MinWidth = size;
        Width = size;
    }

    // Возвращает ширину/минимальную ширину прямоугольного вида; высотой занимается SetPlaylistVisibility(true), он помнит
    // прежнюю высоту до скрытия плейлиста.
    private void RestoreRectangularWidth()
    {
        MinWidth = 400; // как задан MinWidth окна в XAML
        Width = DefaultWindowWidth;
    }

    // Клэмп в рабочую область после смены вида (см. SetPlayerViewMode): MakeWindowSquare/RestoreRectangularWidth меняют
    // только Width/Height, и окно у правого/нижнего края могло выйти за экран. Магнитного прилипания здесь нет.
    private void ClampWindowToWorkArea()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        if (WindowState != WindowState.Normal) return;

        // Left/Top/ActualWidth/ActualHeight — DIP, Screen.WorkingArea — физические пиксели: TransformToDevice даёт тот же
        // пересчёт, что WPF при отрисовке, поэтому клэмп корректен на мониторах с масштабом не 100%.
        var transform = target.TransformToDevice;
        var topLeft = transform.Transform(new Point(Left, Top));
        var size = transform.Transform(new Point(ActualWidth, ActualHeight));

        int left = (int)Math.Round(topLeft.X);
        int top = (int)Math.Round(topLeft.Y);
        int width = (int)Math.Round(size.X);
        int height = (int)Math.Round(size.Y);

        var winBounds = new System.Drawing.Rectangle(left, top, width, height);
        var workArea = System.Windows.Forms.Screen.FromRectangle(winBounds).WorkingArea;

        int clampedLeft = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        int clampedTop = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));

        if (clampedLeft == left && clampedTop == top) return; // уже полностью на экране — трогать нечего

        var deviceToDip = transform;
        deviceToDip.Invert(); // Matrix — struct, копия; Invert() меняет её на месте, а не возвращает новую
        var newTopLeft = deviceToDip.Transform(new Point(clampedLeft, clampedTop));

        Left = newTopLeft.X;
        Top = newTopLeft.Y;
    }

    // Обработчик всех трёх пунктов контекстного меню вида плеера — какой именно вид
    // выбран, определяется по Tag пункта меню ("Square"/"Rectangular"/"Mini").
    private void ViewModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string modeName }) return;
        if (Enum.TryParse<PlayerViewMode>(modeName, out var mode))
            SetPlayerViewMode(mode);
    }

    // Публичная обёртка над SetPlayerViewMode для окна настроек (PlayerViewMode приватный); разбор строки "Square"/
    // "Rectangular"/"Mini" тот же, что в ViewModeMenuItem_Click.
    public void SetPlayerViewModeByName(string modeName)
    {
        if (Enum.TryParse<PlayerViewMode>(modeName, out var mode))
            SetPlayerViewMode(mode);
    }

    // Текущий вид плеера строкой ("Square"/"Rectangular"/"Mini") — чтобы окно настроек могло
    // выставить нужную миниатюру выбранной при открытии, не имея доступа к самому enum.
    public string CurrentViewModeName => _viewMode.ToString();

    // Левый клик по заголовку "Lumisense" открывает то же контекстное меню, что и правый (ContextMenu на элементе делает
    // это только для правого клика).
    private void TitleClickArea_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;
        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void MainViewContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ApplyMainViewContextMenuAccent();
        UpdateViewModeMenuChecks();

        // App.xaml локализует Popup в тот же момент. Повтор после ContextIdle гарантирует,
        // что новый локальный шаблон трёх MenuItem увидит окончательное IsChecked.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(UpdateViewModeMenuChecks));
    }

    // ContextMenu открывается в собственном Popup-дереве и может не унаследовать акцент: публикуем локальные ресурсы,
    // чтобы Fluent CheckBox пунктов выбора вида брал текущий цвет Lumisense.
    private void ApplyMainViewContextMenuAccent()
    {
        Color accent = GetResolvedAccentColor();
        MainViewContextMenu.Resources["SystemAccentColor"] = accent;
        MainViewContextMenu.Resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        MainViewContextMenu.Resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(accent);
    }

    private void UpdateViewModeMenuChecks()
    {
        SquareViewMenuItem.IsCheckable = true;
        RectangularViewMenuItem.IsCheckable = true;
        MiniViewMenuItem.IsCheckable = true;
        SquareViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Square;
        RectangularViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Rectangular;
        MiniViewMenuItem.IsChecked = _viewMode == PlayerViewMode.Mini;
    }
}
