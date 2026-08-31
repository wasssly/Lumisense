using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lumisense;

// Список известных необязательных действий контекстного меню строки трека. «Воспроизвести»
// намеренно отсутствует: базовое действие всегда доступно, а список настроек не зависит от
// локализованных подписей MenuItem и остаётся совместим при их изменении.
public sealed class TrackContextMenuActions : INotifyPropertyChanged
{
    public const string Favorite = "Favorite";
    public const string ShowInExplorer = "ShowInExplorer";
    public const string CopyTrackName = "CopyTrackName";
    public const string CopyPath = "CopyPath";
    public const string CopyFile = "CopyFile";
    public const string Properties = "Properties";
    public const string EditTags = "EditTags";
    public const string NormalizeFileName = "NormalizeFileName";
    public const string RemoveFromPlaylist = "RemoveFromPlaylist";
    public const string DeleteFromDisk = "DeleteFromDisk";
    public const string PlayNext = "PlayNext";
    public const string AddToQueue = "AddToQueue";
    public const string FindFile = "FindFile";

    public static readonly TrackContextMenuActions Instance = new();

    private static readonly Dictionary<string, string> KnownActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        [Favorite] = Favorite,
        [ShowInExplorer] = ShowInExplorer,
        [CopyTrackName] = CopyTrackName,
        [CopyPath] = CopyPath,
        [CopyFile] = CopyFile,
        [Properties] = Properties,
        [EditTags] = EditTags,
        [NormalizeFileName] = NormalizeFileName,
        [RemoveFromPlaylist] = RemoveFromPlaylist,
        [DeleteFromDisk] = DeleteFromDisk,
        [PlayNext] = PlayNext,
        [AddToQueue] = AddToQueue,
        [FindFile] = FindFile
    };

    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
    private int _epoch;

    private TrackContextMenuActions() { }

    // Epoch используется как источник WPF Binding: путь к строке трека не меняется, но после
    // переключения настройки все уже созданные ContextMenu сразу пересчитывают Visibility.
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

// Binding получает Epoch TrackContextMenuActions.Instance только как сигнал обновления;
// конкретная видимость определяется идентификатором, указанным в ConverterParameter.
public sealed class TrackContextMenuActionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => parameter is string actionId && TrackContextMenuActions.Instance.IsEnabled(actionId)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
