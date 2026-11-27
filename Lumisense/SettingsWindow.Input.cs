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

        // Колесо над открытым списком листает его самого; в любом другом месте список просто закрывается.
        if (OpenComboBox() is { } combo)
        {
            if (e.OriginalSource is DependencyObject src && PresentationSource.FromDependencyObject(src) != PresentationSource.FromVisual(this))
            {
                var inList = FindVisualAncestor<System.Windows.Controls.ScrollViewer>(e.OriginalSource as DependencyObject);
                if (inList is null || inList.ScrollableHeight <= 0) e.Handled = true;
                return;
            }

            combo.IsDropDownOpen = false;
        }

        var scrollViewer = FindVisualAncestor<System.Windows.Controls.ScrollViewer>(e.OriginalSource as DependencyObject)
                           ?? PART_ContentScroll;
        if (scrollViewer.ScrollableHeight <= 0) return;

        ScrollByWheel(scrollViewer, -e.Delta * 0.45);
        e.Handled = true;
    }

    private static System.Windows.Controls.ComboBox? OpenComboBox() => Keyboard.FocusedElement switch
    {
        System.Windows.Controls.ComboBox { IsDropDownOpen: true } combo => combo,
        System.Windows.Controls.ComboBoxItem item =>
            System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(item) as System.Windows.Controls.ComboBox
            is { IsDropDownOpen: true } owner ? owner : null,
        _ => null
    };

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

}
