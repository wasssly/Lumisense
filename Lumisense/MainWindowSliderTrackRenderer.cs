using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WpfSlider = System.Windows.Controls.Slider;

namespace Lumisense;

/// <summary>Рисует фоновую и акцентную части дорожки одним DrawingContext — без светлых швов от двух Border
/// с полукруглыми углами на дробном DPI.</summary>
public sealed class MainWindowSliderTrackRenderer : FrameworkElement
{
    public static readonly DependencyProperty BackgroundBrushProperty =
        DependencyProperty.Register(
            nameof(BackgroundBrush),
            typeof(Brush),
            typeof(MainWindowSliderTrackRenderer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(
            nameof(FillBrush),
            typeof(Brush),
            typeof(MainWindowSliderTrackRenderer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private WpfSlider? _slider;

    public Brush? BackgroundBrush
    {
        get => (Brush?)GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public MainWindowSliderTrackRenderer()
    {
        Loaded += MainWindowSliderTrackRenderer_Loaded;
        Unloaded += MainWindowSliderTrackRenderer_Unloaded;
    }

    private void MainWindowSliderTrackRenderer_Loaded(object sender, RoutedEventArgs e)
    {
        AttachToSlider();
        Dispatcher.BeginInvoke(InvalidateVisual, DispatcherPriority.Loaded);
    }

    private void MainWindowSliderTrackRenderer_Unloaded(object sender, RoutedEventArgs e) => DetachFromSlider();

    private void AttachToSlider()
    {
        DetachFromSlider();
        _slider = TemplatedParent as WpfSlider;
        if (_slider is null) return;

        _slider.ValueChanged += Slider_VisualStateChanged;
        _slider.SizeChanged += Slider_SizeChanged;
    }

    private void DetachFromSlider()
    {
        if (_slider is null) return;

        _slider.ValueChanged -= Slider_VisualStateChanged;
        _slider.SizeChanged -= Slider_SizeChanged;
        _slider = null;
    }

    private void Slider_VisualStateChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => InvalidateVisual();

    private void Slider_SizeChanged(object sender, SizeChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (_slider is null ||
            _slider.Template?.FindName("PART_Track", _slider) is not Track track ||
            track.Thumb is not Thumb thumb ||
            track.ActualWidth <= 0 ||
            track.ActualHeight <= 0 ||
            thumb.ActualWidth <= 0 ||
            _slider.Maximum <= _slider.Minimum)
        {
            return;
        }

        Point trackOrigin;
        try
        {
            trackOrigin = track.TransformToVisual(this).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        const double trackHeight = 5.0;
        double top = trackOrigin.Y + (track.ActualHeight - trackHeight) / 2.0;
        double width = track.ActualWidth;
        if (width <= 0) return;

        var fullTrack = new Rect(trackOrigin.X, top, width, trackHeight);
        drawingContext.DrawRoundedRectangle(BackgroundBrush, null, fullTrack, trackHeight / 2.0, trackHeight / 2.0);

        double availableLength = Math.Max(0, width - thumb.ActualWidth);
        double progress = Math.Clamp((_slider.Value - _slider.Minimum) / (_slider.Maximum - _slider.Minimum), 0.0, 1.0);
        double fillWidth = Math.Min(width, thumb.ActualWidth / 2.0 + availableLength * progress);
        if (fillWidth <= 0 || FillBrush is null) return;

        var fillTrack = _slider.IsDirectionReversed
            ? new Rect(trackOrigin.X + width - fillWidth, top, fillWidth, trackHeight)
            : new Rect(trackOrigin.X, top, fillWidth, trackHeight);

        drawingContext.DrawRoundedRectangle(FillBrush, null, fillTrack, trackHeight / 2.0, trackHeight / 2.0);
    }
}
