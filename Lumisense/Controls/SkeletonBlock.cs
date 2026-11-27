using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lumisense;

// Заглушка скелетона: серая плашка с бегущим бликом; из них собирается форма будущего содержимого (SkeletonLines, статистика).
// Для коротких действий без формы результата остаётся MaterialLoadingIndicator.
public sealed class SkeletonBlock : Border
{
    // Наследуется по дереву: достаточно выставить на контейнере скелетона, чтобы выключить блик у всех плашек внутри.
    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.RegisterAttached(
        "IsAnimationEnabled", typeof(bool), typeof(SkeletonBlock),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits,
            (d, _) => (d as SkeletonBlock)?.UpdateAnimation()));

    public static bool GetIsAnimationEnabled(DependencyObject element) => (bool)element.GetValue(IsAnimationEnabledProperty);

    public static void SetIsAnimationEnabled(DependencyObject element, bool value) => element.SetValue(IsAnimationEnabledProperty, value);

    private readonly TranslateTransform _shimmer = new();

    public SkeletonBlock()
    {
        CornerRadius = new CornerRadius(6);
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            BuildBrush();
            UpdateAnimation();
        };
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private void BuildBrush()
    {
        if (Background is not null) return;

        Color baseColor = TryFindResource("TextFillColorPrimary") is Color color ? color : Colors.Gray;
        Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            RelativeTransform = _shimmer,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(26, baseColor.R, baseColor.G, baseColor.B), 0.0),
                new GradientStop(Color.FromArgb(64, baseColor.R, baseColor.G, baseColor.B), 0.5),
                new GradientStop(Color.FromArgb(26, baseColor.R, baseColor.G, baseColor.B), 1.0),
            },
        };
    }

    private void UpdateAnimation()
    {
        if (IsVisible && GetIsAnimationEnabled(this))
        {
            _shimmer.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _shimmer.BeginAnimation(TranslateTransform.XProperty, null);
            _shimmer.X = 0;
        }
    }
}
