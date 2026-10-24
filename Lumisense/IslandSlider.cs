using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumisense;

// Ползунок для меню острова: тонкая полоса, заливка и бегунок. Мышь забирает себе целиком, чтобы клик не доходил до острова.
internal sealed class IslandSlider : FrameworkElement
{
    private const double TrackHeight = 4, ThumbSize = 12;

    private double _value;
    private bool _dragging;

    public double Minimum { get; set; }

    public double Maximum { get; set; } = 1;

    public double Step { get; set; } = 0.01;

    public double WheelStep { get; set; } = 0.01;

    // Значение, к которому возвращает правая кнопка мыши.
    public double Default { get; set; }

    public Color Accent { get; set; } = Colors.White;

    public double Value
    {
        get => _value;
        set
        {
            value = Snap(value);
            if (value == _value) return;

            _value = value;
            InvalidateVisual();
        }
    }

    // Меняет значение пользователь (мышь, колесо, сброс), а не код.
    public event Action<double>? ValueEdited;

    private double Snap(double value) =>
        Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum);

    private double RatioOf(double value) => Maximum > Minimum ? (value - Minimum) / (Maximum - Minimum) : 0;

    private void Edit(double value)
    {
        value = Snap(value);
        if (Math.Abs(value - _value) < 1e-9) return;

        _value = value;
        InvalidateVisual();
        ValueEdited?.Invoke(value);
    }

    private void EditAt(MouseEventArgs e)
    {
        double span = Math.Max(ActualWidth - ThumbSize, 1);
        double ratio = Math.Clamp((e.GetPosition(this).X - ThumbSize / 2) / span, 0, 1);
        Edit(Minimum + ratio * (Maximum - Minimum));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragging = CaptureMouse();
        EditAt(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) EditAt(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _dragging = false;

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Edit(Default);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        Edit(_value + Math.Sign(e.Delta) * WheelStep);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Прозрачная заливка, чтобы мышь ловила всю высоту, а не только тонкую полосу.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        double span = Math.Max(width - ThumbSize, 0), center = height / 2;
        double thumbX = ThumbSize / 2 + span * RatioOf(_value);
        var track = new SolidColorBrush(Color.FromArgb(51, 255, 255, 255));
        dc.DrawRoundedRectangle(track, null, new Rect(ThumbSize / 2, center - TrackHeight / 2, span, TrackHeight), TrackHeight / 2, TrackHeight / 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(Accent), null,
            new Rect(ThumbSize / 2, center - TrackHeight / 2, thumbX - ThumbSize / 2, TrackHeight), TrackHeight / 2, TrackHeight / 2);
        dc.DrawEllipse(Brushes.White, null, new Point(thumbX, center), ThumbSize / 2, ThumbSize / 2);
    }
}
