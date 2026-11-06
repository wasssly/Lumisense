using System.Windows;

namespace Lumisense;

// Вид главного окна (AppSettings.MainWindowLayout): Modern, Glass, Vinyl, Compact, Poster (классический убран).
// Это один набор элементов с разными размерами и порядком кнопок, поэтому остальной код окна о виде не знает.
public partial class MainWindow
{
    // Новые виды (все, кроме классического): размеры, ряд кнопок и высота окна общие, отличия — в MainWindow.Layouts.cs.
    private bool IsModernLayout => _settings.MainWindowLayout is "Modern" or "Glass" or "Vinyl" or "Compact" or "Poster";

    private string LayoutName => _settings.MainWindowLayout;

    // Кнопки, которые можно ставить по бокам от «Назад / Пуск / Вперёд». Первые четыре живут в этом ряду всегда, остальные
    // переезжают из своих мест (шапка плейлиста, строка громкости, заголовок трека) и возвращаются, когда их убирают из слотов.
    internal static readonly IReadOnlyList<string> ModernSlotActions =
        ["Shuffle", "Repeat", "Stop", "Mini", "Favorite", "Mute", "Speed", "Settings", "More", "Statistics", "Playlist", "Queue", "Lyrics"];

    private static readonly string[] TransportSlotActions = ["Shuffle", "Repeat", "Stop", "Mini"];

    internal const int ModernSlotCount = 4;

    // Приводит список слотов к четырём допустимым значениям без повторов: повторная кнопка не может стоять в двух местах.
    internal static List<string> NormalizeModernSlots(IEnumerable<string>? slots)
    {
        var result = new List<string>(ModernSlotCount);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? slot in (slots ?? []).Take(ModernSlotCount))
        {
            string value = slot is not null && ModernSlotActions.Contains(slot) && !(AppEdition.IsLite && slot == "Statistics") && used.Add(slot) ? slot : string.Empty;
            result.Add(value);
        }

        while (result.Count < ModernSlotCount) result.Add(string.Empty);
        return result;
    }

    private Wpf.Ui.Controls.Button? ModernSlotButton(string action) => action switch
    {
        "Shuffle" => ShuffleButton,
        "Repeat" => RepeatButton,
        "Stop" => StopButton,
        "Mini" => MiniModeButton,
        "Favorite" => FavoritesButton,
        "Mute" => MuteButton,
        "Speed" => PlaybackControlButton,
        "Settings" => SettingsButton,
        "More" => MoreActionsButton,
        "Statistics" => StatisticsButton,
        "Playlist" => TogglePlaylistButton,
        "Queue" => QueueButton,
        "Lyrics" => LyricsPanelButton,
        _ => null,
    };

    // Где кнопка жила в разметке и как была оформлена: нужно, чтобы вернуть её на место, когда её убирают из слотов.
    private sealed record SlotButtonHome(
        System.Windows.Controls.Panel Parent, int Index, double Width, double Height,
        Thickness Margin, Thickness Padding, Wpf.Ui.Controls.ControlAppearance Appearance, object? Content);

    private readonly Dictionary<string, SlotButtonHome> _slotButtonHomes = new();

    private void CaptureSlotButtonHomes()
    {
        if (_slotButtonHomes.Count > 0) return;

        foreach (string action in ModernSlotActions)
        {
            if (TransportSlotActions.Contains(action) || ModernSlotButton(action) is not { } button) continue;
            if (button.Parent is not System.Windows.Controls.Panel parent) continue;

            _slotButtonHomes[action] = new SlotButtonHome(
                parent, parent.Children.IndexOf(button), button.Width, button.Height, button.Margin, button.Padding, button.Appearance, button.Content);
        }
    }


    // Вызывается из окна настроек при смене вида: пересчитывает размеры и подгоняет высоту окна под новую обложку.
    public void ApplyMainWindowLayoutLive()
    {
        ApplyContentScale(_isFullscreenLayout || _viewMode == PlayerViewMode.Square);
        UpdateContentMaxWidth();
        if (_isMiniMode) return;

        if (_isPlaylistVisible)
        {
            // Окну нужен запас под обложку нового размера; прежнюю (возможно, большую) высоту не уменьшаем.
            double minHeight = MinHeightWithPlaylist;
            if (_viewMode == PlayerViewMode.Square)
                minHeight = Math.Max(minHeight, Math.Min(SquareMinHeightWithPlaylist, SystemParameters.WorkArea.Height - 40));

            MinHeight = minHeight;
            if (Height < minHeight) Height = minHeight;
        }
        else
        {
            // Плейлист свёрнут: высоту окна задаёт только содержимое, поэтому замеряем её заново.
            FitWindowToCollapsedPlaylist();
        }

        ClampWindowToWorkArea();
    }
}
