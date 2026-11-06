using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lumisense;

internal partial class IslandPlayerWindow
{
    // Гистерезис входа и выхода (px) не даёт карточке приёма мигать.
    private const double AttractRange = 150, DropEnterDistance = 30, DropExitDistance = 80;
    private const double AttractLeanReach = 0.9, AttractPullReach = 0.85, HotLeanReach = 0.2, HotLeanMax = 20;
    private const double CatcherMargin = 40;
    private static readonly TimeSpan DragWatchInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan OrbFlight = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan OrbStagger = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan DropResultHold = TimeSpan.FromMilliseconds(1500);

    private readonly DispatcherTimer _dragWatch = new(DispatcherPriority.Background) { Interval = DragWatchInterval };
    private bool _dragActive, _dropHot, _dropBusy;
    private double _attractPull, _attractLean;
    private State _dropReturn = State.Compact;
    private string[] _dragPaths = [];
    private string _dragSubtitle = "";
    private string _dragIcon = "IconAddFile";
    private int _dropToken;

    private void InitializeDrop() => _dragWatch.Tick += DragWatch_Tick;

    private void StartDropWatch() => _dragWatch.Start();

    private void ShutdownDrop()
    {
        _dragWatch.Stop();
        StopDropPulse();
    }


    // Windows считает целью drag&drop только окно с непрозрачными пикселями под курсором, поэтому зона приёма включается лишь при зажатой кнопке рядом с островом.
    private void DragWatch_Tick(object? sender, EventArgs e)
    {
        bool wanted = _dragActive || (Settings.IslandAcceptDrops && !IsAway && IsVisible && _grab == Grab.None && !_pressed
            && (GetAsyncKeyState(VkLButton) & 0x8000) != 0 && IsCursorNearIsland());
        var visibility = wanted ? Visibility.Visible : Visibility.Collapsed;
        if (DropCatcher.Visibility != visibility) DropCatcher.Visibility = visibility;
    }

    private bool IsCursorNearIsland()
    {
        if (!GetCursorPos(out NativePoint cursor)) return false;

        try
        {
            Point local = Island.PointFromScreen(new Point(cursor.X, cursor.Y));
            double left = (Island.Width - DropCatcher.Width) / 2 - CatcherMargin;
            return local.X >= left && local.X <= left + DropCatcher.Width + 2 * CatcherMargin
                && local.Y >= -CatcherMargin && local.Y <= DropCatcher.Height + CatcherMargin;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }


    private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!Settings.IslandAcceptDrops || !TryReadDropPaths(e.Data, out string[] paths))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _dragPaths = paths;
        _dragActive = true;
        _dropToken++;
        DescribeDrop(paths);
        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
        UpdateDrag(e.GetPosition(Island));
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!_dragActive)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
        UpdateDrag(e.GetPosition(Island));
    }

    private void Window_DragLeave(object sender, System.Windows.DragEventArgs e) => EndDrag();

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        Point at = e.GetPosition(Island);
        string[] paths = _dragPaths.Length > 0 ? _dragPaths : TryReadDropPaths(e.Data, out string[] read) ? read : [];
        _dragActive = false;
        _attractPull = _attractLean = 0;

        if (paths.Length == 0 || !Settings.IslandAcceptDrops)
        {
            LeaveDropCard();
            return;
        }

        BackgroundTask.FireAndForget(AcceptDropAsync(paths, at), nameof(Window_Drop));
    }

    private static bool TryReadDropPaths(System.Windows.IDataObject data, out string[] paths)
    {
        paths = [];
        try
        {
            if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return false;
            if (data.GetData(System.Windows.DataFormats.FileDrop) is not string[] read || read.Length == 0) return false;

            paths = read;
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    private void DescribeDrop(string[] paths)
    {
        bool anyFolder = false;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                anyFolder = true;
                break;
            }
        }

        _dragIcon = anyFolder ? "IconAddFolder" : "IconAddFile";
        if (paths.Length == 1)
        {
            string name = Path.GetFileName(paths[0].TrimEnd('\\', '/'));
            _dragSubtitle = name.Length > 0 ? name : paths[0];
        }
        else
        {
            _dragSubtitle = LocalizationService.Format("Элементов: {0}", paths.Length);
        }

        ShowDropText(LocalizationService.Translate("Отпустите, чтобы добавить"), _dragSubtitle, _dragIcon);
    }

    private void ShowDropText(string title, string subtitle, string icon)
    {
        DropTitle.Text = title;
        DropSubtitle.Text = subtitle;
        DropSubtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DropIcon.Icon != icon) DropIcon.Icon = icon;
    }


    private void UpdateDrag(Point cursor)
    {
        if (IsAway) return;

        State rest = _dropHot ? _dropReturn : _state;
        Shape shape = ShapeOf(rest);
        var rect = new Rect((HostWidth - shape.Width) / 2, 0, shape.Width, shape.Height);
        double dx = cursor.X < rect.Left ? rect.Left - cursor.X : cursor.X > rect.Right ? cursor.X - rect.Right : 0;
        double dy = cursor.Y > rect.Bottom ? cursor.Y - rect.Bottom : 0;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double offset = cursor.X - HostWidth / 2;

        if (!_dropHot && distance <= DropEnterDistance) EnterDropCard(rest);
        else if (_dropHot && distance > DropExitDistance) LeaveDropCard();

        if (_dropHot)
        {
            _attractPull = 0;
            _attractLean = Math.Clamp(offset * HotLeanReach, -HotLeanMax, HotLeanMax);
        }
        else
        {
            double weight = Math.Clamp(1 - distance / AttractRange, 0, 1);
            weight = weight * weight * (3 - 2 * weight);
            _attractLean = offset * weight * AttractLeanReach;
            _attractPull = Math.Max(0, cursor.Y - rect.Bottom) * weight * AttractPullReach;
        }

        UpdateTargets();
    }

    private void EnterDropCard(State rest)
    {
        _dropHot = true;
        _dropReturn = rest == State.Open ? State.Open : State.Compact;
        Color accent = _host.GetResolvedAccentColor();
        DropPulse.Stroke = new SolidColorBrush(accent);
        DropDisc.Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        ShowDropText(LocalizationService.Translate("Отпустите, чтобы добавить"), _dragSubtitle, _dragIcon);
        SetState(State.Drop);
        StartDropPulse();
    }

    private void LeaveDropCard()
    {
        _attractPull = _attractLean = 0;
        if (!_dropHot)
        {
            UpdateTargets();
            return;
        }

        _dropHot = false;
        StopDropPulse();
        if (_state == State.Drop) SetState(_dropReturn);
        else UpdateTargets();
    }

    private void EndDrag()
    {
        if (!_dragActive) return;

        _dragActive = false;
        if (_dropBusy)
        {
            _attractPull = _attractLean = 0;
            UpdateTargets();
            return;
        }

        LeaveDropCard();
    }


    private async Task AcceptDropAsync(string[] paths, Point from)
    {
        int token = ++_dropToken;
        _dropBusy = true;
        try
        {
            if (!_dropHot)
            {
                EnterDropCard(_state);
                _dropHot = true;
            }

            ShowDropText(LocalizationService.Translate("Добавляю…"), _dragSubtitle, _dragIcon);
            LaunchOrbs(from, new Point(HostWidth / 2, 24), Math.Clamp(paths.Length, 1, 4));

            Task<DroppedAddResult> add = _host.AddDroppedPathsAsync(paths);
            if (!ReduceMotion) await Task.Delay(OrbFlight);
            Swallow();
            DroppedAddResult result = await add;

            ShowDropResult(result);
            await Task.Delay(DropResultHold);
            if (token != _dropToken) return;
        }
        catch (Exception)
        {
            _dropBusy = false;
            LeaveDropCard();
            throw;
        }
        finally
        {
            _dropBusy = false;
        }

        if (!_dragActive && _dropHot) LeaveDropCard();
    }

    private void ShowDropResult(DroppedAddResult result)
    {
        StopDropPulse();
        if (result.Added > 0)
        {
            Color accent = _host.GetResolvedAccentColor();
            DropDisc.Fill = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
            ShowDropText(LocalizationService.Translate("Добавлено"), LocalizationService.Format("Треков: {0}", result.Added), "IconCheckmark");
        }
        else if (result.FoundSupported)
        {
            ShowDropText(LocalizationService.Translate("Уже в плейлисте"), "", "IconCheckmark");
        }
        else
        {
            ShowDropText(LocalizationService.Translate("Ничего не найдено"), LocalizationService.Translate("Нет поддерживаемых аудиофайлов"), _dragIcon);
        }

        PopDropDisc();
    }

    private void Swallow()
    {
        if (ReduceMotion) return;

        _height.Velocity -= 150;
        _width.Velocity += 160;
        StartLoop();
    }


    private void StartDropPulse()
    {
        if (ReduceMotion) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var ring = new DoubleAnimation(0.82, 1.5, TimeSpan.FromMilliseconds(1200)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        DropPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, ring);
        DropPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, ring);
        DropPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, 0, TimeSpan.FromMilliseconds(1200)) { RepeatBehavior = RepeatBehavior.Forever });

        var breathe = new DoubleAnimation(0.94, 1.06, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        DropDiscScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        DropDiscScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
    }

    private void StopDropPulse()
    {
        DropPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DropPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DropPulse.BeginAnimation(OpacityProperty, null);
        DropDiscScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DropDiscScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void PopDropDisc()
    {
        if (ReduceMotion) return;

        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 } };
        DropDiscScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        DropDiscScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void LaunchOrbs(Point from, Point to, int count)
    {
        if (ReduceMotion) return;

        Color accent = _host.GetResolvedAccentColor();
        for (int i = 0; i < count; i++)
        {
            double spread = (i - (count - 1) / 2.0) * 14;
            var start = new Point(from.X + spread - 7, from.Y - 7);
            var move = new TranslateTransform(start.X, start.Y);
            var size = new ScaleTransform(1.2, 1.2);
            var orb = new System.Windows.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { size, move } },
                Opacity = 0,
                Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Colors.White, 0),
                        new GradientStop(Color.FromArgb(0xCC, accent.R, accent.G, accent.B), 0.45),
                        new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)
                    }
                }
            };
            Panel.SetZIndex(orb, 50);
            Island.Children.Add(orb);

            TimeSpan delay = TimeSpan.FromTicks(OrbStagger.Ticks * i);
            var x = new DoubleAnimation(start.X, to.X - 7, OrbFlight) { BeginTime = delay, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
            var y = new DoubleAnimation(start.Y, to.Y - 7, OrbFlight) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var shrink = new DoubleAnimation(1.2, 0.35, OrbFlight) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = OrbFlight };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.15)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.8)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            var target = orb;
            fade.Completed += (_, _) => Island.Children.Remove(target);

            move.BeginAnimation(TranslateTransform.XProperty, x);
            move.BeginAnimation(TranslateTransform.YProperty, y);
            size.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            size.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            orb.BeginAnimation(OpacityProperty, fade);
        }
    }


    private const int VkLButton = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
