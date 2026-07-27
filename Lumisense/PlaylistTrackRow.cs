namespace AudioPlayer;

// Строка трека для единого виртуализируемого ListView (и обычного плейлиста с группировкой
// по Folder, и "Избранного" — там Folder указывает на _favoritesFolder ради общего шаблона).
// Раньше каждая папка рендерилась своим вложенным ListView без виртуализации — на больших
// плейлистах это ощутимо тормозило запуск.
public sealed class PlaylistTrackRow
{
    public required PlaylistFolder Folder { get; init; }
    public required string FilePath { get; init; }

    // 1-based номер трека внутри своей папки, считается заранее при построении списка
    public required int IndexInFolder { get; init; }
}
