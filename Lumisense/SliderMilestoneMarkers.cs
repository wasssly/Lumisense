using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AudioPlayer;

/// <summary>
/// Включает точки дискретных значений только у Slider, которые явно помечены в XAML.
/// Координаты берутся из фактического PART_Track и Thumb, поэтому первая и последняя
/// точки совпадают с реальными достижимыми положениями бегунка, а не с шириной контейнера.
/// </summary>
public static class SliderMilestoneMarkers
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SliderMilestoneMarkers),
            new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>
    /// Интервал только для визуальных ориентиров. Значение 0 использует TickFrequency Slider.
    /// Это позволяет, например, оставить точность выбора ширины в 10 px, но рисовать точки
    /// лишь через 50 px.
    /// </summary>
    public static readonly DependencyProperty MarkerFrequencyProperty =
        DependencyProperty.RegisterAttached(
            "MarkerFrequency",
            typeof(double),
            typeof(SliderMilestoneMarkers),
            new PropertyMetadata(0d, OnMarkerFrequencyChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetMarkerFrequency(DependencyObject element) => (double)element.GetValue(MarkerFrequencyProperty);

    public static void SetMarkerFrequency(DependencyObject element, double value) => element.SetValue(MarkerFrequencyProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Slider slider) return;

        slider.Loaded -= Slider_InvalidateMilestones;
        slider.SizeChanged -= Slider_SizeChanged;
        slider.ValueChanged -= Slider_ValueChanged;

        if ((bool)e.NewValue)
        {
            slider.Loaded += Slider_InvalidateMilestones;
            slider.SizeChanged += Slider_SizeChanged;
            slider.ValueChanged += Slider_ValueChanged;
        }

        InvalidateMilestones(slider);
    }

    private static void OnMarkerFrequencyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is Slider slider) InvalidateMilestones(slider);
    }

    private static void Slider_InvalidateMilestones(object sender, RoutedEventArgs e)
    {
        if (sender is Slider slider) InvalidateMilestones(slider);
    }

    private static void Slider_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Slider slider) InvalidateMilestones(slider);
    }

    private static void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider slider) InvalidateMilestones(slider);
    }

    private static void InvalidateMilestones(Slider slider)
    {
        // Attached property задаётся во время XAML-инициализации, когда Slider.Template ещё
        // может быть null. Loaded обработчик повторит invalidate уже после создания template.
        if (slider.Template?.FindName("PART_MilestoneOverlay", slider) is SliderMilestoneOverlay overlay)
            overlay.InvalidateVisual();
    }
}

/// <summary>
/// Лёгкий template-элемент: рисует мягкие точки поверх дорожки, не участвуя в hit testing.
/// </summary>
public sealed class SliderMilestoneOverlay : FrameworkElement
{
    public static readonly DependencyProperty MarkerBrushProperty =
        DependencyProperty.Register(
            nameof(MarkerBrush),
            typeof(Brush),
            typeof(SliderMilestoneOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? MarkerBrush
    {
        get => (Brush?)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (TemplatedParent is not Slider slider ||
            !SliderMilestoneMarkers.GetIsEnabled(slider) ||
            slider.Orientation != Orientation.Horizontal ||
            slider.TickFrequency <= 0 ||
            slider.Maximum <= slider.Minimum)
        {
            return;
        }

        double markerFrequency = SliderMilestoneMarkers.GetMarkerFrequency(slider);
        if (markerFrequency <= 0) markerFrequency = slider.TickFrequency;
        if (markerFrequency <= 0) return;

        if (slider.Template?.FindName("PART_Track", slider) is not Track track ||
            track.Thumb is not Thumb thumb ||
            track.ActualWidth <= 0 ||
            thumb.ActualWidth <= 0)
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

        double availableTrackLength = track.ActualWidth - thumb.ActualWidth;
        if (availableTrackLength < 0) return;

        // Заметная контрольная точка: после увеличения скорректирована на 0,5 DIP.
        const double radius = 3.0;
        Brush markerBrush = MarkerBrush ?? Brushes.White;
        double centerY = trackOrigin.Y + track.ActualHeight / 2.0;
        double epsilon = markerFrequency * 0.0001;

        for (double value = slider.Minimum; value <= slider.Maximum + epsilon; value += markerFrequency)
        {
            double clampedValue = Math.Min(value, slider.Maximum);
            // Текущий шаг уже обозначен крупным Thumb; повторная точка внутри него только
            // утяжелила бы визуальный образ и не добавила информации.
            if (Math.Abs(slider.Value - clampedValue) <= epsilon) continue;

            double relativePosition = (clampedValue - slider.Minimum) / (slider.Maximum - slider.Minimum);
            double centerX = trackOrigin.X + thumb.ActualWidth / 2.0 + availableTrackLength * relativePosition;
            drawingContext.DrawEllipse(markerBrush, null, new Point(centerX, centerY), radius, radius);
        }
    }
}
