using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace AudioPlayer;

// Централизованные параметры доступности. Масштаб применяется к корневому visual tree
// каждого окна, а не только к наследуемому FontSize: большая часть интерфейса Lumisense
// содержит явно заданные размеры, поэтому один FontSize почти не даёт заметного эффекта.
internal static class AccessibilityPreferences
{
    public const double MinimumInterfaceScale = 0.85;
    public const double MaximumInterfaceScale = 1.35;
    public const double DefaultBaseFontSize = 14.0;

    private sealed class WindowScaleState
    {
        public required FrameworkElement ContentRoot { get; init; }
        public required Transform OriginalLayoutTransform { get; init; }
        public required ScaleTransform ScaleTransform { get; init; }
        public required Transform AppliedLayoutTransform { get; init; }
        public required double BaseWidth { get; init; }
        public required double BaseHeight { get; init; }
        public required double BaseMinWidth { get; init; }
        public required double BaseMinHeight { get; init; }
    }

    // ConditionalWeakTable не удерживает закрытые окна в памяти и позволяет сохранить их
    // исходные размеры: переключение 100% → 135% → 100% возвращает ровно исходную геометрию.
    private static readonly ConditionalWeakTable<Window, WindowScaleState> ScaleStates = new();

    public static double NormalizeScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, MinimumInterfaceScale, MaximumInterfaceScale) : 1.0;

    public static void ApplyToWindow(Window window, AppSettings settings)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        double scale = NormalizeScale(settings.InterfaceScale);
        WindowScaleState state = ScaleStates.GetValue(window, CreateScaleState);

        // До этой версии применялся только FontSize. Сбрасываем именно заданный Lumisense
        // базовый размер, чтобы не получить двойное увеличение текста поверх LayoutTransform.
        window.FontSize = DefaultBaseFontSize;
        state.ScaleTransform.ScaleX = scale;
        state.ScaleTransform.ScaleY = scale;

        // LayoutTransform меняет и явно заданные размеры, отступы, иконки и шрифты. Масштаб
        // окна меняем в той же операции на UI-потоке, поэтому изменённый размер не обрезается
        // и пользователь видит результат сразу, без повторного открытия окна.
        if (window.WindowState != WindowState.Maximized)
        {
            window.MinWidth = ScaleDimension(state.BaseMinWidth, scale);
            window.MinHeight = ScaleDimension(state.BaseMinHeight, scale);
            window.Width = ScaleDimension(state.BaseWidth, scale);
            window.Height = ScaleDimension(state.BaseHeight, scale);
        }
    }

    public static bool ShouldReduceMotion(AppSettings settings) => settings?.ReduceMotion == true;

    private static WindowScaleState CreateScaleState(Window window)
    {
        if (window.Content is not FrameworkElement contentRoot)
            throw new InvalidOperationException("Не удалось найти корневой элемент окна для масштабирования интерфейса.");

        Transform originalTransform = contentRoot.LayoutTransform ?? Transform.Identity;
        var scaleTransform = new ScaleTransform(1.0, 1.0);
        Transform appliedTransform;

        if (originalTransform.Value.IsIdentity)
        {
            appliedTransform = scaleTransform;
        }
        else
        {
            var transforms = new TransformGroup();
            transforms.Children.Add(originalTransform);
            transforms.Children.Add(scaleTransform);
            appliedTransform = transforms;
        }

        contentRoot.LayoutTransform = appliedTransform;
        return new WindowScaleState
        {
            ContentRoot = contentRoot,
            OriginalLayoutTransform = originalTransform,
            ScaleTransform = scaleTransform,
            AppliedLayoutTransform = appliedTransform,
            BaseWidth = NormalizeWindowDimension(window.Width),
            BaseHeight = NormalizeWindowDimension(window.Height),
            BaseMinWidth = NormalizeWindowDimension(window.MinWidth),
            BaseMinHeight = NormalizeWindowDimension(window.MinHeight)
        };
    }

    private static double NormalizeWindowDimension(double value) =>
        double.IsFinite(value) && value >= 0 ? value : 0;

    private static double ScaleDimension(double baseValue, double scale) =>
        baseValue > 0 ? baseValue * scale : baseValue;
}
