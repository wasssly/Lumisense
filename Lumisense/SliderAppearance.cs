using System.ComponentModel;

namespace Lumisense;

// Общий переключатель вида всех Slider (AppSettings.SliderStyle): шаблоны слайдеров подписаны на IsMaterial через DataTrigger,
// поэтому смена вида применяется сразу во всех открытых окнах.
public sealed class SliderAppearance : INotifyPropertyChanged
{
    public static SliderAppearance Instance { get; } = new();

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
