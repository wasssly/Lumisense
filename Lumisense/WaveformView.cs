using System.Windows;
using System.Windows.Media;

namespace Lumisense;

// Полоса воспроизведения в виде формы звука (как у SoundCloud) — альтернатива обычному
// Slider'у, см. AppSettings.ProgressBarStyle == "Waveform" и MainWindow.ApplyProgressBarStyle.
// Сам по себе только рисует уже готовый набор пиков (см. WaveformGenerator — там же, откуда
// они берутся) — ничего не знает ни про воспроизведение, ни про перемотку: клики/перетаскивание
// по-прежнему обрабатывает тот же самый прозрачный Border поверх (см. MainWindow.xaml,
// ProgressOverlay_*), что и раньше для обычного Slider'а. IsHitTestVisible="False" в XAML —
// этот элемент чисто визуальный.
//
// Простой FrameworkElement с ручным OnRender, а не Control/ItemsControl с шаблоном — рисовать
// сотни одинаковых прямоугольников через полноценные визуальные элементы (Rectangle/Border на
// каждый бар) ощутимо тяжелее для WPF, чем один проход DrawRoundedRectangle в OnRender, а
// перерисовывать этот элемент нужно часто — на каждый тик таймера прогресса (несколько раз в
// секунду, см. MainWindow.ProgressTimer_Tick).
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Нормализованные (0..1) пики амплитуды — см. WaveformGenerator.GenerateAsync. null или
    // пустой массив — данные ещё не посчитаны (трек только что загрузился) или их не удалось
    // получить (см. комментарий в OnRender про "заглушку").
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

    // Цвет уже проигранной части — акцентный цвет приложения, задаётся из кода (см.
    // MainWindow.RefreshAccentDependentIcons), а не через DynamicResource на системный акцент:
    // приложение поддерживает свой собственный акцент (AppSettings.AccentColorMode == "Manual"),
    // который системными ресурсами темы не покрывается.
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

    // Доля ширины "ведра" (одного деления пиков), уходящая на зазор между барами — то, что
    // визуально отличает форму волны от сплошной заливки. 0.35 подобрано на глаз: зазор заметен
    // на обычных значениях WaveformGenerator.BucketCount, но бары не превращаются в тонкие
    // редкие палочки.
    private const double GapRatio = 0.35;

    // Минимальная высота бара даже у полностью тихого места в треке (тишина/фейд) — без этого
    // такие участки исчезали бы совсем, и полоса выглядела бы визуально "прерванной", как будто
    // сломалась отрисовка, а не просто передаёт настоящую тишину в записи.
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
            // Пики ещё не посчитаны (или не удалось) — тонкая плоская линия по центру вместо
            // пустоты: так область не "прыгает" в размере/виде, когда данные всё же подгрузятся,
            // и сразу видно, что это ещё не готовая, а не сломанная полоса.
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

            // Бар может лежать точно на границе "проиграно/не проиграно" — красим по его
            // центру, а не левому краю, чтобы переход выглядел ровно посередине бара, ближе к
            // тому, где на глаз должна проходить граница прогресса.
            Brush brush = (x + barWidth / 2) <= progressX ? PlayedBrush : UnplayedBrush;

            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), cornerRadius, cornerRadius);
        }
    }
}
