using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumisense;

// Меню «Ещё» (три точки): классическое всплывающее окно или Dynamic menu (designbookmark.com/motion), где поверхность на
// пружинах вырастает из самой кнопки, а пункты выезжают по очереди. Вид выбирается в настройках (AppSettings.MoreMenuSpring).
public partial class MainWindow
{
    // Запас вокруг поверхности: пружина ширины и высоты перелетает цель, а окно Popup имеет фиксированный размер.
    private const double MoreMenuOvershoot = 24;
    private const double MoreMenuItemRise = 18;
    private const double MoreMenuFirstDelay = 0.08;
    private const double MoreMenuItemDelay = 0.05;

    private SpringTrack? _moreMenuTrack;
    private bool _moreMenuSpring;
    private UIElement[] _moreMenuItems = [];
    private System.Windows.Size _moreMenuFinal;

    private static CustomPopupPlacement[] PlaceMoreMenu(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset) =>
        [new CustomPopupPlacement(new System.Windows.Point(targetSize.Width - popupSize.Width, 0), PopupPrimaryAxis.None)];

    // Вызывается перед открытием: выбирает вид меню и приводит разметку Popup к нему.
    private void PrepareMoreMenu()
    {
        ResetMoreMenu();
        _moreMenuSpring = _settings.MoreMenuSpring;
        if (!_moreMenuSpring)
        {
            MoreActionsPopup.Placement = PlacementMode.Bottom;
            MoreActionsPopup.VerticalOffset = 6;
            MoreActionsPopup.PopupAnimation = PopupAnimation.Fade;
            return;
        }

        MoreActionsPopup.CustomPopupPlacementCallback = PlaceMoreMenu;
        MoreActionsPopup.Placement = PlacementMode.Custom;
        MoreActionsPopup.VerticalOffset = 0;
        MoreActionsPopup.PopupAnimation = PopupAnimation.None;
        MoreMenuHeader.Visibility = Visibility.Visible;
        MoreMenuSurface.Padding = new Thickness(8, 0, 0, 8);
        MoreMenuSurface.ClipToBounds = true;
        MoreActionsItemsPanel.Margin = new Thickness(0, 0, 8, 0);
        SetMoreMenuCross(1);

        // Итоговый размер меряем по целой разметке, а окну Popup даём этот размер с запасом на перелёт пружины.
        MoreMenuSurface.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        System.Windows.Size final = MoreMenuSurface.DesiredSize;
        _moreMenuFinal = final;
        MoreMenuHost.Width = final.Width + MoreMenuOvershoot;
        MoreMenuHost.Height = final.Height + MoreMenuOvershoot;
        if (SpringMotion.Reduced(null)) return;

        _moreMenuItems = [.. MoreActionsItemsPanel.Children.OfType<UIElement>().Where(item => item.Visibility == Visibility.Visible)];
        MoreMenuSurface.Width = MoreActionsButton.ActualWidth;
        MoreMenuSurface.Height = MoreActionsButton.ActualHeight;
        foreach (UIElement item in _moreMenuItems)
        {
            item.Opacity = 0;
            item.RenderTransform = new TranslateTransform(0, MoreMenuItemRise);
        }

        SetMoreMenuCross(0);
    }

    private void SetMoreMenuCross(double x)
    {
        x = Math.Clamp(x, -0.1, 1.1);
        MoreMenuLineTop.RenderTransform = MenuLineTransform(-4 * (1 - x), 45 * x);
        MoreMenuLineBottom.RenderTransform = MenuLineTransform(4 * (1 - x), -45 * x);
    }

    // Две линии в 8 px друг от друга сходятся в центре и поворачиваются на 45° в разные стороны.
    private static Transform MenuLineTransform(double shift, double angle)
    {
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(angle));
        group.Children.Add(new TranslateTransform(0, shift));
        return group;
    }

    private void MoreActionsPopup_Opened(object? sender, EventArgs e)
    {
        if (!_moreMenuSpring || SpringMotion.Reduced(null)) return;

        System.Windows.Size final = _moreMenuFinal;
        var width = new SpringValue { Value = MoreMenuSurface.Width, Target = final.Width };
        var height = new SpringValue { Value = MoreMenuSurface.Height, Target = final.Height };
        var cross = new SpringValue { Value = 0, Target = 1 };
        MotionTokens.MenuWidth.ApplyTo(width);
        MotionTokens.MenuHeight.ApplyTo(height);
        MotionTokens.MenuCross.ApplyTo(cross);

        UIElement[] items = _moreMenuItems;
        var itemSprings = new SpringValue[items.Length];
        var started = new bool[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            itemSprings[i] = new SpringValue { Value = 0, Target = 0 };
            MotionTokens.MenuItem.ApplyTo(itemSprings[i]);
        }

        Stopwatch clock = Stopwatch.StartNew();
        _moreMenuTrack = new SpringTrack(
            () =>
            {
                MoreMenuSurface.Width = Math.Max(0, width.Value);
                MoreMenuSurface.Height = Math.Max(0, height.Value);
                MoreMenuSurface.CornerRadius = new CornerRadius(4 + 4 * Math.Clamp(cross.Value, 0, 1));
                SetMoreMenuCross(cross.Value);

                double elapsed = clock.Elapsed.TotalSeconds;
                for (int i = 0; i < items.Length; i++)
                {
                    if (!started[i] && elapsed >= MoreMenuFirstDelay + i * MoreMenuItemDelay)
                    {
                        started[i] = true;
                        itemSprings[i].Target = 1;
                    }

                    double s = itemSprings[i].Value;
                    items[i].Opacity = Math.Clamp(s, 0, 1);
                    if (items[i].RenderTransform is TranslateTransform shift) shift.Y = (1 - s) * MoreMenuItemRise;
                }
            },
            [width, height, cross, .. itemSprings])
        {
            KeepAlive = () => Array.Exists(started, begun => !begun),
            Settled = FinishMoreMenuFrame,
        };
        SpringRunner.Start(_moreMenuTrack);
    }

    // Меню доехало до размера: выставляем авторазмер, чтобы поверхность следовала за содержимым (например, при смене языка).
    private void FinishMoreMenuFrame()
    {
        MoreMenuSurface.ClearValue(FrameworkElement.WidthProperty);
        MoreMenuSurface.ClearValue(FrameworkElement.HeightProperty);
        MoreMenuSurface.CornerRadius = new CornerRadius(8);
        SetMoreMenuCross(1);
        foreach (UIElement item in _moreMenuItems)
        {
            item.ClearValue(UIElement.OpacityProperty);
            item.ClearValue(UIElement.RenderTransformProperty);
        }
    }

    private void MoreActionsPopup_Closed(object? sender, EventArgs e) => ResetMoreMenu();

    private void ResetMoreMenu()
    {
        if (_moreMenuTrack is not null)
        {
            SpringRunner.Stop(_moreMenuTrack, finish: false);
            _moreMenuTrack = null;
        }

        MoreMenuSurface.ClearValue(FrameworkElement.WidthProperty);
        MoreMenuSurface.ClearValue(FrameworkElement.HeightProperty);
        MoreMenuHost.ClearValue(FrameworkElement.WidthProperty);
        MoreMenuHost.ClearValue(FrameworkElement.HeightProperty);
        MoreMenuSurface.CornerRadius = new CornerRadius(8);
        MoreMenuSurface.Padding = new Thickness(8);
        MoreMenuSurface.ClipToBounds = false;
        MoreActionsItemsPanel.Margin = new Thickness(0);
        MoreMenuHeader.Visibility = Visibility.Collapsed;
        foreach (UIElement item in MoreActionsItemsPanel.Children.OfType<UIElement>())
        {
            item.ClearValue(UIElement.OpacityProperty);
            item.ClearValue(UIElement.RenderTransformProperty);
        }

        _moreMenuItems = [];
    }

    private void MoreMenuHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        MoreActionsPopup.IsOpen = false;
        e.Handled = true;
    }
}
