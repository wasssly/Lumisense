using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lumisense;

// Список известных необязательных пунктов контекстного меню мини-плеера (см.
// MiniPlayerWindow.xaml, Border.ContextMenu). В отличие от TrackContextMenuActions здесь нет
// пункта, который обязан оставаться всегда доступным: даже "Настройки" можно скрыть — они
// по-прежнему открываются из контекстного меню основного окна и из значка в трее.
public sealed class MiniPlayerContextMenuActions : INotifyPropertyChanged
{
    public const string Settings = "Settings";
    public const string NowPlaying = "NowPlaying";
    public const string Pin = "Pin";
    public const string Topmost = "Topmost";
    public const string OverlayCompatibility = "OverlayCompatibility";
    public const string SecondaryButton = "SecondaryButton";
    public const string PlaybackRate = "PlaybackRate";
    public const string Pitch = "Pitch";
    public const string Opacity = "Opacity";

    public static readonly MiniPlayerContextMenuActions Instance = new();

    private static readonly Dictionary<string, string> KnownActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        [Settings] = Settings,
        [NowPlaying] = NowPlaying,
        [Pin] = Pin,
        [Topmost] = Topmost,
        [OverlayCompatibility] = OverlayCompatibility,
        [SecondaryButton] = SecondaryButton,
        [PlaybackRate] = PlaybackRate,
        [Pitch] = Pitch,
        [Opacity] = Opacity
    };

    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
    private int _epoch;

    private MiniPlayerContextMenuActions() { }

    // Epoch — источник WPF Binding: путь к MiniPlayerWindow не меняется, но после переключения
    // настройки уже открытое меню (и следующее его открытие) сразу пересчитывает Visibility.
    public int Epoch => _epoch;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsDisabled(string? actionId) =>
        actionId is not null && KnownActionIds.TryGetValue(actionId, out string? knownId) && _disabled.Contains(knownId);

    public bool IsEnabled(string? actionId) => !IsDisabled(actionId);

    public void Initialize(IEnumerable<string>? disabledActionIds)
    {
        var normalized = NormalizeDisabledActions(disabledActionIds);
        if (_disabled.SetEquals(normalized)) return;

        _disabled.Clear();
        foreach (string actionId in normalized)
            _disabled.Add(actionId);
        Bump();
    }

    public void SetDisabled(string? actionId, bool disabled)
    {
        if (actionId is null || !KnownActionIds.TryGetValue(actionId, out string? knownId)) return;

        bool changed = disabled ? _disabled.Add(knownId) : _disabled.Remove(knownId);
        if (changed) Bump();
    }

    public List<string> GetDisabledActionIds() => _disabled.OrderBy(actionId => actionId, StringComparer.Ordinal).ToList();

    public static List<string> NormalizeDisabledActions(IEnumerable<string>? actionIds)
    {
        if (actionIds is null) return new List<string>();

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? actionId in actionIds)
        {
            if (actionId is not null && KnownActionIds.TryGetValue(actionId, out string? knownId))
                normalized.Add(knownId);
        }

        return normalized.OrderBy(actionId => actionId, StringComparer.Ordinal).ToList();
    }

    private void Bump()
    {
        _epoch++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Epoch)));
    }
}

// Binding получает Epoch MiniPlayerContextMenuActions.Instance только как сигнал обновления;
// конкретная видимость определяется идентификатором, указанным в ConverterParameter.
public sealed class MiniPlayerContextMenuActionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => parameter is string actionId && MiniPlayerContextMenuActions.Instance.IsEnabled(actionId)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Тот же приём, что и TrackContextMenuGroupVisibilityConverter: разделитель между секциями
// меню скрывается вместе со всей секцией, которую он открывает, — иначе при отключении в
// настройках всех пунктов одной секции разделители по обе стороны от пустой секции остаются
// на месте. ConverterParameter — идентификаторы через "|"; видимо, если хотя бы один включён.
public sealed class MiniPlayerContextMenuGroupVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string actionIds) return Visibility.Collapsed;

        foreach (string actionId in actionIds.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (MiniPlayerContextMenuActions.Instance.IsEnabled(actionId))
                return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
