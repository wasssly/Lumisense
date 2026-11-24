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
    public void ApplyAccessibilityPreferences() => AccessibilityPreferences.ApplyToWindow(this, _settings);

    private bool _isAutoScrolling;
    private Cursor? _previousCursor;
    private Point _autoScrollOrigin;
    private System.Windows.Controls.ScrollViewer? _autoScrollViewer;
    private bool _autoScrollHooked;
    private double _autoScrollVelocity, _autoScrollOffset;
    private long _autoScrollLastFrame;

    private void SettingsWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        if (_isAutoScrolling)
        {
            StopAutoScrolling();
            e.Handled = true;
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        _autoScrollViewer = FindVisualAncestor<System.Windows.Controls.ScrollViewer>(source)
                             ?? PART_ContentScroll;
        if (_autoScrollViewer is null || _autoScrollViewer.ScrollableHeight <= 0) return;

        _autoScrollOrigin = e.GetPosition(RootLayout);
        _previousCursor = Cursor;
        _isAutoScrolling = true;
        Mouse.Capture(this, CaptureMode.SubTree);
        Cursor = Cursors.ScrollNS;
        StopWheelScroll();
        _autoScrollOffset = _autoScrollViewer.VerticalOffset;
        _autoScrollVelocity = 0;
        _autoScrollLastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_autoScrollHooked)
        {
            _autoScrollHooked = true;
            CompositionTarget.Rendering += AutoScrollFrame;
        }

        e.Handled = true;
    }

    private void SettingsWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isAutoScrolling || _autoScrollViewer is null) return;

        Point current = Mouse.GetPosition(RootLayout);
        double distance = current.Y - _autoScrollOrigin.Y;
        Cursor = distance < -4 ? Cursors.ScrollN : distance > 4 ? Cursors.ScrollS : Cursors.ScrollNS;
        e.Handled = true;
    }

    private void SettingsRoot_LostMouseCapture(object sender, MouseEventArgs e)
    {
        StopAutoScrolling();
    }

    // Автопрокрутка идёт по кадрам отрисовки, а не по таймеру: скорость сглаживается, поэтому страница разгоняется и тормозит плавно,
    // а положение не округляется до пикселя на каждом тике. Чем дальше курсор от точки запуска, тем быстрее страница.
    private const double AutoScrollDeadZone = 8, AutoScrollMaxSpeed = 2000, AutoScrollSmoothingSeconds = 0.12;

    private void AutoScrollFrame(object? sender, EventArgs e)
    {
        if (!_isAutoScrolling || _autoScrollViewer is null) return;

        double dt = Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(_autoScrollLastFrame).TotalSeconds, 0.05);
        _autoScrollLastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
        if (dt <= 0) return;

        double distance = Mouse.GetPosition(RootLayout).Y - _autoScrollOrigin.Y;
        double effective = Math.Max(0, Math.Abs(distance) - AutoScrollDeadZone);
        double target = Math.Sign(distance) * Math.Min(AutoScrollMaxSpeed, 6.5 * effective + 0.015 * effective * effective);
        _autoScrollVelocity += (target - _autoScrollVelocity) * (1 - Math.Exp(-dt / AutoScrollSmoothingSeconds));
        if (Math.Abs(_autoScrollVelocity) < 0.5 && target == 0) return;

        _autoScrollOffset = Math.Clamp(_autoScrollOffset + _autoScrollVelocity * dt, 0, _autoScrollViewer.ScrollableHeight);
        _autoScrollViewer.ScrollToVerticalOffset(_autoScrollOffset);
    }

    private void StopAutoScrolling()
    {
        if (!_isAutoScrolling && !_autoScrollHooked) return;
        _isAutoScrolling = false;
        if (_autoScrollHooked)
        {
            _autoScrollHooked = false;
            CompositionTarget.Rendering -= AutoScrollFrame;
        }

        _autoScrollVelocity = 0;
        if (Mouse.Captured is not null) Mouse.Capture(null);
        Cursor = _previousCursor ?? Cursors.Arrow;
        _previousCursor = null;
        _autoScrollViewer = null;
    }

    private void SettingsRoot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Вертикальные Slider (в первую очередь эквалайзер) используют колесо для изменения
        // своего значения и сами помечают событие обработанным. Не перехватываем их здесь.
        if (FindVisualAncestor<System.Windows.Controls.Slider>(e.OriginalSource as DependencyObject) is not null)
            return;

        var scrollViewer = FindVisualAncestor<System.Windows.Controls.ScrollViewer>(e.OriginalSource as DependencyObject)
                           ?? PART_ContentScroll;
        if (scrollViewer.ScrollableHeight <= 0) return;

        ScrollByWheel(scrollViewer, -e.Delta * 0.45);
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (current is T match) return match;

            // TextBlock.Text часто отдаёт Run как OriginalSource, а он FrameworkContentElement, не Visual:
            // VisualTreeHelper.GetParent для него бросает InvalidOperationException.
            current = current switch
            {
                System.Windows.Media.Visual visual =>
                    System.Windows.Media.VisualTreeHelper.GetParent(visual),
                System.Windows.Media.Media3D.Visual3D visual3D =>
                    System.Windows.Media.VisualTreeHelper.GetParent(visual3D),
                System.Windows.FrameworkContentElement contentElement =>
                    contentElement.Parent,
                _ => null
            };
        }

        return null;
    }

    private void InterfaceScaleSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Клик по дорожке обрабатывает сам Slider (IsMoveToPointEnabled); для Thumb блокируем только захват бегунка,
        // чтобы точечный клик менял масштаб без перетаскивания.
        if (e.OriginalSource is Thumb)
            e.Handled = true;
    }

    private void InterfaceScaleSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is Thumb)
            e.Handled = true;
    }

    private void InterfaceScaleSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Slider slider && slider.IsMouseCaptured)
            slider.ReleaseMouseCapture();
    }

    private void InterfaceScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing || InterfaceScaleValueText is null) return;

        _settings.InterfaceScale = AccessibilityPreferences.NormalizeScale(e.NewValue / 100d);
        InterfaceScaleValueText.Text = $"{_settings.InterfaceScale * 100:0}%";

        // Масштаб WPF безопасно применяется при создании окон; перестройка открытых окон на лету может оставить мини-плеер
        // в смешанном DPI/layout (например, 100% → 135% → 100%), поэтому значение сохраняем и применяем после перезапуска.
        if (!_interfaceScaleRestartNoticeShown)
        {
            _interfaceScaleRestartNoticeShown = true;
            var restartDialog = new ScaleRestartDialog(this, _settings);
            restartDialog.ShowDialog();
            // «Позже» относится только к текущему изменению. При следующем изменении
            // масштаба уведомление должно появиться снова.
            _interfaceScaleRestartNoticeShown = false;
        }
    }
}
