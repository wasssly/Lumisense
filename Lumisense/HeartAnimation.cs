using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumisense;

// Короткая анимация поверх кнопки сердечка при добавлении трека в избранное (AppSettings.FavoriteHeartAnimation).
// Рисуется адорнером окна, а не самой кнопкой: строки плейлиста пересоздаются при обновлении списка, а кнопка
// мини-плеера меняет иконку целиком, поэтому анимация не зависит от жизни конкретного элемента.
internal static class HeartAnimation
{
    public const string None = "None";
    public const string Sparks = "Sparks";
    public const string Fill = "Fill";
    public const string Ring = "Ring";

    public static bool IsKnown(string? mode) => mode is None or Sparks or Fill or Ring;

    // Позицию кнопки берём до переключения избранного: после него список может пересоздать строку.
    public static HeartAnchor? Capture(FrameworkElement? button, Window? host)
    {
        if (button is null || host?.Content is not UIElement root || !button.IsVisible) return null;
        try
        {
            Point center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), root);
            return new HeartAnchor(root, center);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static void Play(HeartAnchor? anchor, AppSettings settings)
    {
        if (anchor is null) return;
        string mode = settings.FavoriteHeartAnimation;
        if (mode == None || !IsKnown(mode) || AccessibilityPreferences.ShouldReduceMotion(settings)) return;

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(anchor.Root);
        if (layer is null) return;

        var adorner = new HeartAdorner(anchor.Root, anchor.Center, mode, ResolveAccent(anchor.Root));
        layer.Add(adorner);

        double milliseconds = mode switch { Sparks => 480, Fill => 520, _ => 460 };
        var animation = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) => layer.Remove(adorner);
        adorner.BeginAnimation(HeartAdorner.ProgressProperty, animation);
    }

    private static Brush ResolveAccent(UIElement root)
    {
        if (root is FrameworkElement element)
        {
            foreach (string key in new[] { "AccentFillColorDefaultBrush", "SystemAccentColorPrimaryBrush" })
            {
                if (element.TryFindResource(key) is Brush brush) return brush;
            }
        }
        return Brushes.OrangeRed;
    }

    internal sealed record HeartAnchor(UIElement Root, Point Center);

    private sealed class HeartAdorner : Adorner
    {
        // Форма сердечка (Material, 24x24) для режима «Заливка».
        private static readonly Geometry HeartGeometry = Geometry.Parse(
            "M12 21.35 L10.55 20.03 C5.4 15.36 2 12.28 2 8.5 C2 5.42 4.42 3 7.5 3 C9.24 3 10.91 3.81 12 5.09 " +
            "C13.09 3.81 14.76 3 16.5 3 C19.58 3 22 5.42 22 8.5 C22 12.28 18.6 15.36 13.45 20.04 L12 21.35 Z");

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(HeartAdorner),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        private readonly Point _center;
        private readonly string _mode;
        private readonly Brush _accent;

        public HeartAdorner(UIElement adornedElement, Point center, string mode, Brush accent) : base(adornedElement)
        {
            _center = center;
            _mode = mode;
            _accent = accent;
            IsHitTestVisible = false;
        }

        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        protected override void OnRender(DrawingContext context)
        {
            double t = Progress;
            switch (_mode)
            {
                case Ring: RenderRing(context, t); break;
                case Sparks: RenderSparks(context, t); break;
                case Fill: RenderFill(context, t); break;
            }
        }

        // Одно тонкое кольцо расходится от кнопки и гаснет.
        private void RenderRing(DrawingContext context, double t)
        {
            double radius = 8 + 16 * t;
            var pen = new Pen(_accent, Math.Max(0.5, 2.4 * (1 - t))) { };
            context.PushOpacity(Math.Clamp(0.9 * (1 - t), 0, 1));
            context.DrawEllipse(null, pen, _center, radius, radius);
            context.Pop();
        }

        // Восемь маленьких точек разлетаются по кругу и уменьшаются.
        private void RenderSparks(DrawingContext context, double t)
        {
            const int count = 8;
            double distance = 7 + 17 * t;
            double size = Math.Max(0.4, 2.6 * (1 - t));
            context.PushOpacity(Math.Clamp(1 - t * t, 0, 1));
            for (int i = 0; i < count; i++)
            {
                double angle = Math.PI * 2 * i / count - Math.PI / 2;
                var point = new Point(_center.X + Math.Cos(angle) * distance, _center.Y + Math.Sin(angle) * distance);
                context.DrawEllipse(_accent, null, point, size, size);
            }
            context.Pop();
        }

        // Светлая копия сердечка «наполняется» снизу вверх и гаснет; поверх основной иконки, которая уже заполнена.
        private void RenderFill(DrawingContext context, double t)
        {
            const double size = 16;
            double rise = Math.Clamp(t / 0.62, 0, 1);
            double fade = t < 0.62 ? 1 : Math.Clamp(1 - (t - 0.62) / 0.38, 0, 1);

            context.PushTransform(new TranslateTransform(_center.X - size / 2, _center.Y - size / 2));
            context.PushTransform(new ScaleTransform(size / 24, size / 24));
            double top = 24 * (1 - rise);
            context.PushClip(new RectangleGeometry(new Rect(0, top, 24, 24 - top)));
            context.PushOpacity(0.85 * fade);
            context.DrawGeometry(Brushes.White, null, HeartGeometry);
            context.Pop();
            context.Pop();
            context.Pop();
            context.Pop();
        }
    }
}
