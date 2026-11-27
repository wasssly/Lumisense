namespace Lumisense;

// Единый список форматов, которые плеер открывает и добавляет в плейлист; раньше он дублировался в нескольких местах.
internal static class AudioFormats
{
    public static readonly string[] Extensions =
        [".mp3", ".wav", ".wma", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aiff", ".aif"];
}
