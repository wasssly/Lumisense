namespace Lumisense;

internal static class PlaybackTimeFormat
{
    // Формат "mm:ss" берёт только минутный компонент: час выпадал, и 75:00 показывалось как 15:00.
    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
}
