using System.Windows;
using System.Windows.Media;

namespace AudioPlayer;

// Волнистая (синусоидальная) заливка прогресса в духе Material Design 3 Expressive —
// используется вместо обычной сплошной полосы у ProgressSlider, когда в настройках выбрана
// анимация "MD3" (см. MainWindow.ApplyProgressSliderAnimationMode и WavyProgressSliderStyle
// в App.xaml).
//
// Важный момент про то, ГДЕ и КАК этот элемент оказывается на экране: он ставится не поверх
// всего слайдера целиком, а внутрь DecreaseRepeatButton кастомного ControlTemplate — то есть в
// ту самую часть Track, которую WPF САМ уже растягивает ровно на "заполненный" промежуток, от
// начала трека до текущего положения бегунка (см. App.xaml, WavyProgressSliderStyle). Поэтому
// самому WavyProgressFill вообще не нужно ничего знать про Value/Minimum/Maximum слайдера — он
// просто нужно нарисовать волну на всю СВОЮ собственную ширину (ActualWidth), а какая именно
// это будет ширина — уже забота стандартного механизма Track.
public class WavyProgressFill : FrameworkElement
{
    // Цвет волны — акцентный по умолчанию (в App.xaml привязан к AccentFillColorDefaultBrush,
    // то есть автоматически подстраивается под акцент/тему приложения), но при желании можно
    // задать любой другой Brush.
    public static readonly DependencyProperty WaveColorProperty = DependencyProperty.Register(
        nameof(WaveColor), typeof(Brush), typeof(WavyProgressFill),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush WaveColor
    {
        get => (Brush)GetValue(WaveColorProperty);
        set => SetValue(WaveColorProperty, value);
    }

    // Смещение фазы волны в пикселях. AffectsRender — благодаря этому анимация этого свойства
    // (см. Storyboard в MainWindow.ApplyProgressSliderAnimationMode) сама по себе, безо всякого
    // ручного кода, вызывает перерисовку (OnRender) на каждый кадр. Анимируется от 0 до
    // Wavelength и зацикливается (RepeatBehavior.Forever) — поскольку синусоида периодична,
    // сдвиг ровно на одну длину волны выглядит как непрерывное зацикленное "течение" волны
    // вправо, без видимого скачка в момент, когда анимация начинает новый виток.
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(WavyProgressFill),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    // Амплитуда волны в пикселях — по ТЗ 4-6px, берём середину диапазона.
    public static readonly DependencyProperty AmplitudeProperty = DependencyProperty.Register(
        nameof(Amplitude), typeof(double), typeof(WavyProgressFill),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Amplitude
    {
        get => (double)GetValue(AmplitudeProperty);
        set => SetValue(AmplitudeProperty, value);
    }

    // Длина волны в пикселях — по ТЗ ~40-50px, берём середину диапазона. Storyboard в
    // MainWindow анимирует Phase от 0 ровно до этого значения — см. комментарий у Phase выше.
    public static readonly DependencyProperty WavelengthProperty = DependencyProperty.Register(
        nameof(Wavelength), typeof(double), typeof(WavyProgressFill),
        new FrameworkPropertyMetadata(46.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Wavelength
    {
        get => (double)GetValue(WavelengthProperty);
        set => SetValue(WavelengthProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(WavyProgressFill),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        // Совсем маленькая заполненная часть (трек только начал играть) — рисовать пару
        // пикселей синусоиды бессмысленно, только замусорит угол слайдера обрубком кривой.
        if (width < 2 || height < 2) return;

        double amplitude = Math.Max(0, Amplitude);
        double wavelength = Math.Max(1, Wavelength);
        double midY = height / 2.0;

        // Шаг в 3px — достаточно гладкая на глаз кривая при заметно меньшей нагрузке на кадр,
        // чем шаг в 1px (а перерисовка тут происходит каждый кадр анимации, экономия не лишняя).
        const double step = 3.0;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            bool first = true;
            double x = 0;
            while (true)
            {
                double clampedX = Math.Min(x, width);
                double y = midY + amplitude * Math.Sin(2 * Math.PI * (clampedX + Phase) / wavelength);
                var point = new Point(clampedX, y);

                if (first)
                {
                    ctx.BeginFigure(point, false, false);
                    first = false;
                }
                else
                {
                    ctx.LineTo(point, true, false);
                }

                if (clampedX >= width) break;
                x += step;
            }
        }
        geometry.Freeze();

        var pen = new Pen(WaveColor, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        dc.DrawGeometry(null, pen, geometry);
    }
}
