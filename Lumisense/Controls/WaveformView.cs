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

    public static readonly DependencyProperty HoverRatioProperty = DependencyProperty.Register(
        nameof(HoverRatio), typeof(double?), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // 0..1 — где сейчас курсор над полосой; null — курсора нет. Участок от текущей позиции до курсора подсвечивается
    // бледным цветом проигранной части, как при наведении в SoundCloud.
    public double? HoverRatio
    {
        get => (double?)GetValue(HoverRatioProperty);
        set => SetValue(HoverRatioProperty, value);
    }

    public static readonly DependencyProperty GapRatioProperty = DependencyProperty.Register(
        nameof(GapRatio), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.35, FrameworkPropertyMetadataOptions.AffectsRender));

    // Доля ширины деления под зазор между столбиками (ProgressBarAppearance.WaveformGap); 0.35 подобрано на глаз.
    public double GapRatio
    {
        get => (double)GetValue(GapRatioProperty);
        set => SetValue(GapRatioProperty, value);
    }

    public static readonly DependencyProperty UnplayedOpacityProperty = DependencyProperty.Register(
        nameof(UnplayedOpacity), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Прозрачность ещё не проигранной части (ProgressBarAppearance.WaveformUnplayedOpacity).
    public double UnplayedOpacity
    {
        get => (double)GetValue(UnplayedOpacityProperty);
        set => SetValue(UnplayedOpacityProperty, value);
    }

    public static readonly DependencyProperty IsFlatProperty = DependencyProperty.Register(
        nameof(IsFlat), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // true — прямые столбики вместо скруглённых (ProgressBarAppearance.EndShape).
    public bool IsFlat
    {
        get => (bool)GetValue(IsFlatProperty);
        set => SetValue(IsFlatProperty, value);
    }

    public static readonly DependencyProperty IsCapsuleProperty = DependencyProperty.Register(
        nameof(IsCapsule), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // true — крупные столбики-капсулы фиксированного шага по усреднённым пикам вместо сотни тонких (ProgressBarAppearance.WaveformShape).
    public bool IsCapsule
    {
        get => (bool)GetValue(IsCapsuleProperty);
        set => SetValue(IsCapsuleProperty, value);
    }

    // Минимальная высота бара, чтобы тихие участки не исчезали и полоса не выглядела прерванной.
    private const double MinBarHeight = 2.0;

    // Шаг капсул (ширина столбика + зазор): число столбиков зависит от ширины полосы, а не от числа пиков.
    private const double CapsulePitch = 8.0;

    // Среднее с примесью максимума: чистое среднее занижает громкие участки, чистый максимум делает все столбики одинаково высокими.
    private static float[] Downsample(float[] peaks, int count)
    {
        if (count >= peaks.Length) return peaks;

        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            int start = (int)((long)i * peaks.Length / count);
            int end = Math.Max(start + 1, (int)((long)(i + 1) * peaks.Length / count));
            float sum = 0, max = 0;
            for (int j = start; j < end; j++)
            {
                sum += peaks[j];
                max = Math.Max(max, peaks[j]);
            }

            result[i] = 0.6f * (sum / (end - start)) + 0.4f * max;
        }

        return result;
    }

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
        double? hoverX = HoverRatio is { } hoverRatio ? width * Math.Clamp(hoverRatio, 0.0, 1.0) : null;

        // Клон, а не PlayedBrush.Opacity: PlayedBrush принадлежит MainWindow и менять его нельзя.
        Brush? hoverBrush = null;
        if (hoverX is not null)
        {
            hoverBrush = PlayedBrush.CloneCurrentValue();
            hoverBrush.Opacity = 0.45;
        }

        // Прозрачность непроигранной части: клон, а не UnplayedBrush.Opacity, потому что кисть задана в XAML и общая.
        Brush unplayedBrush = UnplayedBrush;
        double unplayedOpacity = double.IsFinite(UnplayedOpacity) ? Math.Clamp(UnplayedOpacity, 0.0, 1.0) : 1.0;
        if (unplayedOpacity < 0.999)
        {
            unplayedBrush = UnplayedBrush.CloneCurrentValue();
            unplayedBrush.Opacity = UnplayedBrush.Opacity * unplayedOpacity;
        }

        double gapRatio = double.IsFinite(GapRatio) ? Math.Clamp(GapRatio, 0.0, 0.9) : 0.35;
        if (IsCapsule)
            peaks = Downsample(peaks, Math.Max(1, (int)(width / CapsulePitch)));

        double bucketWidth = width / peaks.Length;
        double barWidth = Math.Max(1.0, bucketWidth * (1 - gapRatio));
        double cornerRadius = IsFlat ? 0.0 : barWidth / 2;
        // Минимум равен ширине, чтобы тихий столбик оставался кружком, а не сплющенной капсулой.
        double minBarHeight = IsCapsule ? Math.Max(MinBarHeight, barWidth) : MinBarHeight;

        for (int i = 0; i < peaks.Length; i++)
        {
            double barHeight = Math.Max(minBarHeight, peaks[i] * height);
            double x = i * bucketWidth + (bucketWidth - barWidth) / 2;
            double y = (height - barHeight) / 2;

            // Бар на границе проиграно/нет красим по центру, а не по левому краю, чтобы переход выглядел ровно.
            double center = x + barWidth / 2;
            Brush brush = center <= progressX ? PlayedBrush
                : hoverBrush is not null && center <= hoverX ? hoverBrush
                : unplayedBrush;

            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), cornerRadius, cornerRadius);
        }
    }
}
