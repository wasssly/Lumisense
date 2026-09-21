using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Полоса воспроизведения в виде формы звука — только рисует готовые пики (WaveformGenerator),
// клики обрабатывает Border поверх. Ручной OnRender вместо шаблона: сотни баров, частая перерисовка.
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Нормализованные (0..1) пики, см. WaveformGenerator.GenerateAsync; null/пусто — данные ещё не посчитаны
    // или не получены (см. "заглушку" в OnRender).
    public float[]? Peaks
    {
        get => (float[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // 0..1 — доля трека, которая уже проиграна (столько же несёт и ProgressSlider.Value /
    // ProgressSlider.Maximum, просто в виде готового отношения, а не двух отдельных чисел).
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    // Цвет проигранной части задаётся из кода (MainWindow.RefreshAccentDependentIcons): собственный акцент
    // (AccentColorMode == "Manual") не покрывается системными ресурсами темы.
    public Brush PlayedBrush
    {
        get => (Brush)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public static readonly DependencyProperty UnplayedBrushProperty = DependencyProperty.Register(
        nameof(UnplayedBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    // Цвет ещё не проигранной части — обычный, не завязанный на акцент цвет темы (задаётся
    // прямо в XAML через DynamicResource, см. MainWindow.xaml).
    public Brush UnplayedBrush
    {
        get => (Brush)GetValue(UnplayedBrushProperty);
        set => SetValue(UnplayedBrushProperty, value);
    }

    // Доля ширины деления под зазор между барами; 0.35 подобрано на глаз, чтобы бары не стали тонкими палочками.
    private const double GapRatio = 0.35;

    // Минимальная высота бара, чтобы тихие участки не исчезали и полоса не выглядела прерванной.
    private const double MinBarHeight = 2.0;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var peaks = Peaks;

        if (peaks == null || peaks.Length == 0)
        {
            // Пока пиков нет — тонкая линия по центру: область не "прыгает" при подгрузке данных.
            dc.DrawRectangle(UnplayedBrush, null, new Rect(0, height / 2 - 0.75, width, 1.5));
            return;
        }

        double progressX = width * Math.Clamp(Progress, 0.0, 1.0);

        double bucketWidth = width / peaks.Length;
        double barWidth = Math.Max(1.0, bucketWidth * (1 - GapRatio));
        double cornerRadius = barWidth / 2;

        for (int i = 0; i < peaks.Length; i++)
        {
            double barHeight = Math.Max(MinBarHeight, peaks[i] * height);
            double x = i * bucketWidth + (bucketWidth - barWidth) / 2;
            double y = (height - barHeight) / 2;

            // Бар на границе проиграно/нет красим по центру, а не по левому краю, чтобы переход выглядел ровно.
            Brush brush = (x + barWidth / 2) <= progressX ? PlayedBrush : UnplayedBrush;

            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), cornerRadius, cornerRadius);
        }
    }
}
