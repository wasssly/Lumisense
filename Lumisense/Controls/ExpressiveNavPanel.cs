using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lumisense;

// Пружина со скоростью: значение стремится к цели, а скорость сохраняется при смене цели посреди движения, поэтому быстрые
// повторные переключения не «рвут» анимацию. Шаг интегрируется мелкими кусками, чтобы результат не зависел от частоты кадров.
public sealed class SpringValue
{
    public double Value { get; set; }
    public double Velocity { get; set; }
    public double Target { get; set; }
    public double Stiffness { get; set; } = 400;
    public double DampingRatio { get; set; } = 0.8;

    // true — ещё движется; false — остановилась (значение приравнено к цели).
    public bool Step(double deltaSeconds)
    {
        double damping = 2 * DampingRatio * Math.Sqrt(Stiffness);
        // Кадр с провалом (тяжёлая раскладка или показ страницы в самом начале перехода) не должен «перепрыгивать» движение: по журналу
        // такие провалы 28–67 мс идут в начале, и шаг в 1/30 с сдвигал подсветку сразу на заметную долю пути. Предел за кадр — 25 мс.
        double remaining = Math.Clamp(deltaSeconds, 0, 0.025);
        while (remaining > 0)
        {
            double h = Math.Min(remaining, 1.0 / 240);
            Velocity += (Stiffness * (Target - Value) - damping * Velocity) * h;
            Value += Velocity * h;
            remaining -= h;
        }

        if (Math.Abs(Target - Value) < 0.0004 && Math.Abs(Velocity) < 0.002)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }

        return true;
    }
}

// Панель плавающей навигации. Принцип (как у плавающей панели в Material 3 Expressive): у каждого пункта есть своя «степень
// выбора» reveal (0..1), которую двигает пружина к 1 у выбранного пункта и к 0 у остальных; из неё каждый кадр выводится всё
// остальное — ширина пункта (раскрытие подписи), положения пунктов и скользящая подсветка (взвешенное среднее прямоугольников
// пунктов по их reveal). Всё считается в одном проходе раскладки из одних и тех же значений, поэтому подсветка, иконки и подписи
// не расходятся между собой, а промежуточные пункты при длинном переходе вообще не раскрываются (их reveal остаётся нулевым):
// подсветка едет напрямую от прежнего пункта к новому.
public sealed class ExpressiveNavPanel : Panel
{
    // Раскрывать выбранный пункт до естественной ширины (подпись рядом с иконкой); иначе пункты имеют постоянную ширину.
    public static readonly DependencyProperty ExpandSelectedProperty = DependencyProperty.Register(
        nameof(ExpandSelected), typeof(bool), typeof(ExpressiveNavPanel),
        new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(ExpressiveNavPanel),
        new FrameworkPropertyMetadata(40.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    // Ширина свёрнутого пункта (только иконка).
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ExpressiveNavPanel),
        new FrameworkPropertyMetadata(40.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(ExpressiveNavPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<SpringValue> _reveals = new();
    private double[] _natural = Array.Empty<double>();
    private double[] _lastReveal = Array.Empty<double>();
    private Rect[] _rects = Array.Empty<Rect>();
    private Size[] _clipSizes = Array.Empty<Size>();
    private TranslateTransform?[] _shifts = Array.Empty<TranslateTransform?>();
    private double _totalWidth;
    private int _selectedIndex = -1;
    private bool _animating;
    private TimeSpan _lastFrame = TimeSpan.MinValue;

    // Диагностика плавности: сколько кадров прошло за движение, самый долгий и сколько было медленнее 25 мс. Пишется в журнал,
    // только если были медленные кадры, — по нему видно, дёргается ли панель из-за нагрузки на интерфейс (например, при показе
    // тяжёлой страницы), а не из-за самой анимации.
    private int _frameCount;
    private int _slowFrames;
    private int _slowestFrameIndex;
    private double _maxFrameMilliseconds;

    public ExpressiveNavPanel()
    {
        Unloaded += (_, _) => StopAnimation();
    }

    public bool ExpandSelected
    {
        get => (bool)GetValue(ExpandSelectedProperty);
        set => SetValue(ExpandSelectedProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    // Вызывается для пункта, когда меняется его reveal (0 — не выбран, 1 — выбран): по нему окно плавно перекрашивает иконку
    // и проявляет подпись.
    public Action<UIElement, double>? ProximityChanged { get; set; }

    // true — движение началось, false — закончилось (на время движения окно отключает округление раскладки по пикселям).
    public event Action<bool>? AnimatingChanged;

    public bool IsAnimating => _animating;

    // Точная (дробная) ширина всех пунктов по последней раскладке — окно по ней удерживает панель по центру на дробных долях пикселя.
    public double ExactWidth => _totalWidth;

    // Выбрать пункт: с пружиной (animate) или сразу.
    public void SelectIndex(int index, bool animate)
    {
        EnsureSprings();
        int count = InternalChildren.Count;
        _selectedIndex = count == 0 ? -1 : Math.Clamp(index, 0, count - 1);
        for (int i = 0; i < count; i++) _reveals[i].Target = i == _selectedIndex ? 1 : 0;

        if (animate && IsLoaded && count > 1)
        {
            StartAnimation();
            return;
        }

        StopAnimation();
        for (int i = 0; i < count; i++)
        {
            _reveals[i].Value = _reveals[i].Target;
            _reveals[i].Velocity = 0;
        }

        InvalidateMeasure();
        InvalidateVisual();
        NotifyProximity(force: true);
    }

    private void EnsureSprings()
    {
        int count = InternalChildren.Count;
        while (_reveals.Count < count) _reveals.Add(new SpringValue { Stiffness = 380, DampingRatio = 0.95 });
        while (_reveals.Count > count) _reveals.RemoveAt(_reveals.Count - 1);
    }

    private double Reveal(int index) => index < _reveals.Count ? Math.Clamp(_reveals[index].Value, 0, 1) : 0;

    // Ширина всех пунктов в конечном состоянии (выбранный раскрыт, остальные свёрнуты): окно по ней заранее знает, где в итоге
    // окажется центрированная панель, и плавно приводит содержимое именно туда.
    public double ComputeTargetWidth()
    {
        int count = InternalChildren.Count;
        if (_natural.Length != count) return _totalWidth;

        double scale = PixelScale;
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            double natural = _natural[i];
            if (!ExpandSelected)
            {
                total += natural;
                continue;
            }

            double min = Math.Min(SnapUp(MinItemWidth, scale), natural);
            total += i == _selectedIndex ? natural : min;
        }

        return total;
    }

    private double PixelScale => UseLayoutRounding ? VisualTreeHelper.GetDpi(this).DpiScaleX : 0;

    // Округление вверх до целого числа физических пикселей: в покое все границы пунктов лежат ровно на пикселях, поэтому при
    // остановке нечего «доводить» и нет рывка в конце движения.
    private static double SnapUp(double value, double scale) => scale > 0 ? Math.Ceiling(value * scale - 1e-6) / scale : value;

    private double ItemWidth(int index, double natural, double scale)
    {
        if (!ExpandSelected) return natural;

        double min = Math.Min(SnapUp(MinItemWidth, scale), natural);
        return min + (natural - min) * Reveal(index);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureSprings();
        int count = InternalChildren.Count;
        double height = ItemHeight;
        double scale = PixelScale;
        if (_natural.Length != count) _natural = new double[count];
        if (_rects.Length != count) _rects = new Rect[count];
        if (_clipSizes.Length != count) _clipSizes = new Size[count];
        if (_shifts.Length != count) _shifts = new TranslateTransform?[count];

        // Измерение вложенных пунктов с теми же ограничениями возвращается сразу из кэша WPF, поэтому покадровый вызов дёшев.
        var constraint = new Size(double.PositiveInfinity, height);
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            UIElement child = InternalChildren[i];
            child.Measure(constraint);
            _natural[i] = SnapUp(child.DesiredSize.Width, scale);
            total += ItemWidth(i, _natural[i], scale);
        }

        _totalWidth = total;
        return new Size(total, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = InternalChildren.Count;
        if (_natural.Length != count || _rects.Length != count || _clipSizes.Length != count || _shifts.Length != count)
            return finalSize;

        // Раскладка WPF округляет границы элементов до пикселей, и пункт, едущий на доли пикселя за кадр, шагал бы целыми пикселями
        // (это и было «подёргиванием»). Поэтому пункт ставится на ближайший пиксель, а остаток (до полупикселя) отдаётся
        // преобразованию сдвига, которое не округляется; текст при этом рисуется без привязки к сетке (TextHintingMode=Animated).
        // Подсветка использует точные границы, так что она и пункты совпадают до долей пикселя.
        double scale = PixelScale;
        double radius = ItemHeight / 2;
        double x = 0;
        for (int i = 0; i < count; i++)
        {
            double width = ItemWidth(i, _natural[i], scale);
            _rects[i] = new Rect(x, 0, width, ItemHeight);

            double left = x, right = x + width;
            if (scale > 0)
            {
                left = Math.Round(left * scale) / scale;
                right = Math.Round(right * scale) / scale;
            }

            var slot = new Rect(left, 0, Math.Max(0, right - left), ItemHeight);
            UIElement child = InternalChildren[i];
            child.Arrange(slot);

            TranslateTransform shift = _shifts[i] ??= new TranslateTransform();
            if (!ReferenceEquals(child.RenderTransform, shift)) child.RenderTransform = shift;
            double remainder = x - left;
            if (Math.Abs(shift.X - remainder) > 0.0001) shift.X = remainder;

            // WPF раскладывает элемент, которому тесно, на его полную ширину и лишь обрезает по прямоугольнику ячейки. Свёрнутый
            // пункт поэтому рисовал фон наведения целиком (с прямым срезом справа). Обрезаем пункт по скруглённой ячейке:
            // наведение и подпись ограничены капсулой/кругом.
            if (_clipSizes[i].Width != slot.Width || _clipSizes[i].Height != slot.Height)
            {
                var clip = new RectangleGeometry(new Rect(0, 0, slot.Width, slot.Height), radius, radius);
                clip.Freeze();
                child.Clip = clip;
                _clipSizes[i] = slot.Size;
            }

            x += width;
        }

        return finalSize;
    }

    // Пункты уходят в другую панель (таблетки, боковая панель): снимаем с них обрезку и сдвиг, иначе чужой вид унаследует
    // «свёрнутую» ячейку (подписи таблеток пропадали).
    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is UIElement removed)
        {
            removed.ClearValue(UIElement.ClipProperty);
            removed.ClearValue(UIElement.RenderTransformProperty);
        }

        _clipSizes = Array.Empty<Size>();
        _shifts = Array.Empty<TranslateTransform?>();
    }

    // Подсветка — среднее прямоугольников пунктов, взвешенное по их reveal: у прежнего и нового пункта веса дополняют друг друга,
    // и подсветка линейно переезжает от одного к другому, не заходя в промежуточные.
    private Rect ComputeHighlight(int count)
    {
        if (count == 0) return Rect.Empty;

        double sum = 0, x = 0, width = 0;
        for (int i = 0; i < count; i++)
        {
            double weight = Reveal(i);
            sum += weight;
            x += weight * _rects[i].X;
            width += weight * _rects[i].Width;
        }

        if (sum < 0.001)
        {
            if (_selectedIndex < 0 || _selectedIndex >= count) return Rect.Empty;
            return _rects[_selectedIndex];
        }

        return new Rect(x / sum, 0, width / sum, ItemHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        // Подсветка считается здесь, из прямоугольников последней раскладки и текущих reveal: так при постоянных ширинах она
        // движется без повторной раскладки.
        int count = InternalChildren.Count;
        if (HighlightBrush is not { } brush || _rects.Length != count) return;

        Rect highlight = ComputeHighlight(count);
        if (highlight.IsEmpty || highlight.Width <= 0) return;

        double radius = highlight.Height / 2;
        drawingContext.DrawRoundedRectangle(brush, null, highlight, radius, radius);
    }

    private void StartAnimation()
    {
        if (_animating) return;

        _animating = true;
        _lastFrame = TimeSpan.MinValue;
        _frameCount = 0;
        _slowFrames = 0;
        _slowestFrameIndex = 0;
        _maxFrameMilliseconds = 0;
        CompositionTarget.Rendering += OnFrame;
        AnimatingChanged?.Invoke(true);
    }

    private void StopAnimation()
    {
        if (!_animating) return;

        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
        if (_slowFrames > 0)
        {
            // Номер самого долгого кадра: 2–3 — провал в самом начале (показ страницы), больше — посреди движения панели.
            Logger.Info($"Навигация: кадров {_frameCount}, самый долгий {_maxFrameMilliseconds:0} мс (кадр №{_slowestFrameIndex}), медленнее 25 мс: {_slowFrames}");
        }

        AnimatingChanged?.Invoke(false);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastFrame) return;

        double delta = _lastFrame == TimeSpan.MinValue ? 1.0 / 60 : (args.RenderingTime - _lastFrame).TotalSeconds;
        _lastFrame = args.RenderingTime;

        _frameCount++;
        double milliseconds = delta * 1000;
        if (milliseconds > _maxFrameMilliseconds)
        {
            _maxFrameMilliseconds = milliseconds;
            _slowestFrameIndex = _frameCount;
        }

        if (milliseconds > 25) _slowFrames++;

        bool moving = false;
        foreach (SpringValue spring in _reveals)
            moving |= spring.Step(delta);

        // Пока ширины пунктов постоянны (только иконки или подписи под иконками), раскладка не меняется — перерисовывается лишь
        // подсветка. Раскладка на каждом кадре нужна, только когда выбранный пункт раскрывается.
        if (ExpandSelected) InvalidateMeasure();
        InvalidateVisual();
        NotifyProximity(force: false);
        if (!moving) StopAnimation();
    }

    private void NotifyProximity(bool force)
    {
        Action<UIElement, double>? callback = ProximityChanged;
        if (callback is null) return;

        int count = InternalChildren.Count;
        if (_lastReveal.Length != count)
        {
            _lastReveal = new double[count];
            Array.Fill(_lastReveal, double.NaN);
        }

        for (int i = 0; i < count; i++)
        {
            double reveal = Reveal(i);
            if (!force && !double.IsNaN(_lastReveal[i]) && Math.Abs(_lastReveal[i] - reveal) < 0.002) continue;

            _lastReveal[i] = reveal;
            callback(InternalChildren[i], reveal);
        }
    }

    // Сбросить запомненное значение, чтобы следующий вызов обновил все пункты (после смены стиля или перестановки пунктов).
    public void InvalidateProximity() => _lastReveal = Array.Empty<double>();
}

// Рамка с мягкой тенью без BlurEffect/DropShadowEffect: встроенные эффекты WPF считаются программно и пересчитываются на каждом
// кадре, пока размер панели меняется, из-за чего анимация рывками. Здесь тень — несколько полупрозрачных скруглённых
// прямоугольников, это обычная векторная отрисовка.
public sealed class SoftShadowBorder : Border
{
    private const int LayerCount = 9;
    private const double Step = 2;
    private const double OffsetY = 3;

    private static readonly Brush[] Layers = CreateLayers();

    private static Brush[] CreateLayers()
    {
        var layers = new Brush[LayerCount];
        for (int i = 0; i < LayerCount; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb(8, 0, 0, 0));
            brush.Freeze();
            layers[i] = brush;
        }

        return layers;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        Size size = RenderSize;
        double radius = CornerRadius.TopLeft;
        for (int i = LayerCount; i >= 1; i--)
        {
            double spread = i * Step;
            drawingContext.DrawRoundedRectangle(
                Layers[i - 1],
                null,
                new Rect(-spread, -spread + OffsetY, size.Width + 2 * spread, size.Height + 2 * spread),
                radius + spread,
                radius + spread);
        }

        base.OnRender(drawingContext);
    }
}
