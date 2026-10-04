using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lumisense;

// Общий переключатель вида всех Slider (AppSettings.SliderStyle): шаблоны слайдеров подписаны на SliderMaterialConverter,
// поэтому смена вида применяется сразу во всех открытых окнах. Отдельный Slider может не следовать общему виду (Override).
public sealed class SliderAppearance : INotifyPropertyChanged
{
    public static SliderAppearance Instance { get; } = new();

    // null — как в общей настройке; true/false — вид задан принудительно (полоса воспроизведения выбирает его своей настройкой).
    public static readonly DependencyProperty OverrideProperty = DependencyProperty.RegisterAttached(
        "Override", typeof(bool?), typeof(SliderAppearance), new PropertyMetadata(null));

    public static bool? GetOverride(DependencyObject element) => (bool?)element.GetValue(OverrideProperty);

    public static void SetOverride(DependencyObject element, bool? value) => element.SetValue(OverrideProperty, value);

    private bool _isMaterial;

    private SliderAppearance()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsMaterial
    {
        get => _isMaterial;
        private set
        {
            if (_isMaterial == value) return;
            _isMaterial = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMaterial)));
        }
    }

    // "Material" включает новый вид, любое другое значение — обычный.
    public void Apply(string? style) => IsMaterial = style == "Material";
}

// values[0] — общий SliderAppearance.IsMaterial, values[1] — Override конкретного Slider.
public sealed class SliderMaterialConverter : IMultiValueConverter
{
    public static SliderMaterialConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length > 1 && values[1] is bool forced) return forced;
        return values.Length > 0 && values[0] is true;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
