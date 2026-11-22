using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Lumisense;

public partial class SettingsWindow
{
    private const double NavScrollTopThreshold = 2;
    private static readonly TimeSpan NavScrollDuration = TimeSpan.FromMilliseconds(320);

    private double? _navSavedOffset;
    private bool _navWasCheckedOnPress;
    private bool _navScrollAnimating;
    private double _navScrollFrom, _navScrollTo;
    private long _navScrollStart;

    private const double WheelSmoothingSeconds = 0.07;
    private System.Windows.Controls.ScrollViewer? _wheelViewer;
    private double _wheelTarget, _wheelCurrent;
    private long _wheelLastFrame;
    private bool _wheelAnimating;

    private void InitializeNavScroll()
    {
        AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(NavScroll_PreviewMouseDown), true);
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(NavScroll_Click));
        PART_ContentScroll.PreviewMouseWheel += (_, _) =>
        {
            StopNavScroll();
            _navSavedOffset = null;
        };
        Closed += (_, _) =>
        {
            StopNavScroll();
            StopWheelScroll();
        };
    }

    // Колесо двигает цель, а страница догоняет её экспоненциально: частые щелчки складываются, а не рвут анимацию.
    private void ScrollByWheel(System.Windows.Controls.ScrollViewer viewer, double delta)
    {
        if (ReferenceEquals(viewer, PART_ContentScroll))
        {
            StopNavScroll();
            _navSavedOffset = null;
        }

        bool continuing = ReferenceEquals(_wheelViewer, viewer) && _wheelViewer is not null;
        if (!continuing)
        {
            StopWheelScroll();
            _wheelViewer = viewer;
            _wheelCurrent = viewer.VerticalOffset;
            _wheelTarget = _wheelCurrent;
        }

        _wheelTarget = Math.Clamp(_wheelTarget + delta, 0, viewer.ScrollableHeight);

        if (AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow))
        {
            viewer.ScrollToVerticalOffset(_wheelTarget);
            StopWheelScroll();
            return;
        }

        if (_wheelAnimating) return;

        _wheelAnimating = true;
        _wheelLastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += WheelScrollFrame;
    }

    private void WheelScrollFrame(object? sender, EventArgs e)
    {
        if (_wheelViewer is null)
        {
            StopWheelScroll();
            return;
        }

        double dt = Stopwatch.GetElapsedTime(_wheelLastFrame).TotalSeconds;
        _wheelLastFrame = Stopwatch.GetTimestamp();

        double diff = _wheelTarget - _wheelCurrent;
        if (Math.Abs(diff) < 0.5)
        {
            _wheelViewer.ScrollToVerticalOffset(_wheelTarget);
            StopWheelScroll();
            return;
        }

        _wheelCurrent += diff * (1 - Math.Exp(-dt / WheelSmoothingSeconds));
        _wheelViewer.ScrollToVerticalOffset(_wheelCurrent);
    }

    private void StopWheelScroll()
    {
        _wheelViewer = null;
        if (!_wheelAnimating) return;

        _wheelAnimating = false;
        CompositionTarget.Rendering -= WheelScrollFrame;
    }

    private void NavScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _navWasCheckedOnPress = FindNavRadio(e.OriginalSource as DependencyObject)?.IsChecked == true;

    private void NavScroll_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not RadioButton { GroupName: "SettingsNav", IsChecked: true }) return;

        bool repeat = _navWasCheckedOnPress;
        _navWasCheckedOnPress = false;
        if (!repeat) return;

        ToggleContentScroll();
    }

    private void ToggleContentScroll()
    {
        double offset = PART_ContentScroll.VerticalOffset;

        if (_navScrollAnimating)
        {
            if (_navScrollTo <= NavScrollTopThreshold && _navSavedOffset is { } returnTo)
            {
                _navSavedOffset = null;
                AnimateContentScroll(returnTo);
            }
            else if (_navScrollTo > NavScrollTopThreshold)
            {
                _navSavedOffset = _navScrollTo;
                AnimateContentScroll(0);
            }

            return;
        }

        if (offset > NavScrollTopThreshold)
        {
            _navSavedOffset = offset;
            AnimateContentScroll(0);
        }
        else if (_navSavedOffset is { } saved)
        {
            _navSavedOffset = null;
            AnimateContentScroll(Math.Min(saved, PART_ContentScroll.ScrollableHeight));
        }
    }

    private void ResetNavScrollMemory()
    {
        StopNavScroll();
        StopWheelScroll();
        _navSavedOffset = null;
    }

    private void AnimateContentScroll(double target)
    {
        StopWheelScroll();
        if (AccessibilityPreferences.ShouldReduceMotion(_settings, AnimationAreas.SettingsWindow))
        {
            StopNavScroll();
            PART_ContentScroll.ScrollToVerticalOffset(target);
            return;
        }

        _navScrollFrom = PART_ContentScroll.VerticalOffset;
        _navScrollTo = target;
        _navScrollStart = Stopwatch.GetTimestamp();
        if (_navScrollAnimating) return;

        _navScrollAnimating = true;
        CompositionTarget.Rendering += NavScrollFrame;
    }

    private void NavScrollFrame(object? sender, EventArgs e)
    {
        double t = Math.Clamp(Stopwatch.GetElapsedTime(_navScrollStart).TotalMilliseconds / NavScrollDuration.TotalMilliseconds, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        PART_ContentScroll.ScrollToVerticalOffset(_navScrollFrom + (_navScrollTo - _navScrollFrom) * eased);
        if (t >= 1) StopNavScroll();
    }

    private void StopNavScroll()
    {
        if (!_navScrollAnimating) return;

        _navScrollAnimating = false;
        CompositionTarget.Rendering -= NavScrollFrame;
    }

    private static RadioButton? FindNavRadio(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is RadioButton { GroupName: "SettingsNav" } radio) return radio;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
