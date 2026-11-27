namespace Lumisense;

// Разделы, где анимации можно отключить по отдельности (AppSettings.DisabledAnimationAreas). Общий выключатель «Снизить движение»
// по-прежнему отключает всё сразу, а эти значения хранятся как строки, чтобы порядок и состав разделов можно было менять без правки настроек.
internal static class AnimationAreas
{
    public const string Sliders = "Sliders";
    public const string SettingsWindow = "SettingsWindow";
    public const string Cover = "Cover";
    public const string Lyrics = "Lyrics";
    public const string Progress = "Progress";
    public const string NowPlaying = "NowPlaying";
    public const string MiniPlayer = "MiniPlayer";
    public const string Island = "Island";
    public const string Tray = "Tray";
    public const string Heart = "Heart";
    public const string Loading = "Loading";

    public static readonly IReadOnlyList<string> All =
    [
        Sliders, SettingsWindow, Cover, Lyrics, Progress, NowPlaying, MiniPlayer, Island, Tray, Heart, Loading,
    ];

    // Неизвестные и повторяющиеся значения отбрасываются: файл настроек могли править вручную или он пришёл из другой версии.
    public static List<string> Normalize(IEnumerable<string>? ids) =>
        [.. (ids ?? []).Where(id => id is not null && All.Contains(id)).Distinct()];
}
