using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumisense;

internal enum TrayMenuEntryKind
{
    Header,
    Label,
    Item,
    Separator
}

internal sealed record TrayMenuEntry(TrayMenuEntryKind Kind, string? Text = null, string? IconKey = null,
    ImageSource? Image = null, Action? Click = null);

// Меню трея в собственном окне вместо ContextMenu: у ContextMenu нельзя управлять стороной выезда, а здесь
// направление берётся от ближайшего края экрана (где лежит панель задач), и выезд режется границами окна.
public sealed partial class TrayMenuWindow : Window
{
    private const double RevealDistance = 60;
    private const double RevealDurationMs = 300;

    private readonly bool _animate;
    private double _dirX;
    private double _dirY;
    private EventHandler? _revealHandler;
    private bool _closing;

    internal TrayMenuWindow(IEnumerable<TrayMenuEntry> entries, bool animate)
    {
        InitializeComponent();
        _animate = animate;

        foreach (var entry in entries)
            ItemsHost.Children.Add(BuildEntry(entry));
    }

    public void ShowAtCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);

        // Панель задач у ближайшего к значку края; он же задаёт сторону выезда.
        var bounds = screen.Bounds;
        int toLeft = cursor.X - bounds.Left, toRight = bounds.Right - cursor.X;
        int toTop = cursor.Y - bounds.Top, toBottom = bounds.Bottom - cursor.Y;
        int nearest = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
        _dirX = nearest == toLeft ? -1 : nearest == toRight ? 1 : 0;
        _dirY = _dirX != 0 ? 0 : nearest == toTop ? -1 : 1;

        MenuSurface.Opacity = 0;
        Show();

        // Берём фактический размер после SizeToContent, а не заранее измеренный, чтобы снизу не оставалось запаса.
        UpdateLayout();
        double scale = ToastMonitorDpi.GetScale(screen, 1.0);
        int w = (int)Math.Round(ActualWidth * scale);
        int h = (int)Math.Round(ActualHeight * scale);
        var work = screen.WorkingArea;

        int x = _dirX < 0 ? work.Left
            : _dirX > 0 ? work.Right - w
            : Math.Clamp(cursor.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int y = _dirY < 0 ? work.Top
            : _dirY > 0 ? work.Bottom - h
            : Math.Clamp(cursor.Y - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowSnapHelper.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            WindowSnapHelper.SWP_NOZORDER | WindowSnapHelper.SWP_NOACTIVATE);

        // Без активации Deactivated не сработает и меню не закроется кликом мимо.
        Activate();
        StartReveal();
    }

    // Отсчёт идёт со второго кадра: первый при показе окна тяжёлый (HWND, шаблоны, иконки), и анимация по часам с него
    // выглядела бы рывком.
    private void StartReveal()
    {
        if (!_animate)
        {
            MenuSurface.Opacity = 1;
            return;
        }

        var spline = new KeySpline(0.1, 0.9, 0.2, 1.0);

        // p: 0 — поверхность у края и прозрачна, 1 — на месте.
        void Apply(double p)
        {
            SurfaceTranslate.X = _dirX * RevealDistance * (1 - p);
            SurfaceTranslate.Y = _dirY * RevealDistance * (1 - p);
            MenuSurface.Opacity = p;
        }

        Apply(0);
        int frame = 0;
        TimeSpan? start = null;
        _revealHandler = (_, args) =>
        {
            if (++frame < 2 || args is not RenderingEventArgs rendering) return;

            start ??= rendering.RenderingTime;
            double t = Math.Clamp((rendering.RenderingTime - start.Value).TotalMilliseconds / RevealDurationMs, 0, 1);
            Apply(spline.GetSplineProgress(t));
            if (t >= 1) StopReveal();
        };
        CompositionTarget.Rendering += _revealHandler;
    }

    private void StopReveal()
    {
        if (_revealHandler is null) return;
        CompositionTarget.Rendering -= _revealHandler;
        _revealHandler = null;
        SurfaceTranslate.X = 0;
        SurfaceTranslate.Y = 0;
        MenuSurface.Opacity = 1;
    }

    private UIElement BuildEntry(TrayMenuEntry entry)
    {
        if (entry.Kind == TrayMenuEntryKind.Separator)
        {
            var line = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
            line.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
            return line;
        }

        var icon = new ContentControl { Width = 20, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Image is not null)
            icon.Content = new Image { Source = entry.Image, Width = 20, Height = 20 };
        else if (entry.IconKey is not null)
            icon.Content = new SvgPathIcon { Size = 14, Icon = entry.IconKey };

        var text = new TextBlock
        {
            Text = entry.Text,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);

        var button = new Button { Content = row, Style = (Style)Resources["TrayMenuRowStyle"] };

        if (entry.Kind == TrayMenuEntryKind.Item)
        {
            button.Click += (_, _) =>
            {
                CloseMenu();
                // Действие после закрытия: фокус успевает вернуться предыдущему окну.
                if (entry.Click is { } click) Dispatcher.BeginInvoke(click);
            };
            return button;
        }

        // Заголовок и «сейчас играет» — не кнопки, но та же вёрстка строки.
        button.IsHitTestVisible = false;
        button.Focusable = false;
        if (entry.Kind == TrayMenuEntryKind.Header)
        {
            text.FontWeight = FontWeights.SemiBold;
            button.SetResourceReference(Control.ForegroundProperty, "AccentFillColorDefaultBrush");
        }
        else
        {
            text.FontSize = 12;
        }
        return button;
    }

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => CloseMenu();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseMenu();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopReveal();
        base.OnClosed(e);
    }
}
