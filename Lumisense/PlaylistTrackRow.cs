using System.IO;

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

    // Статус вычисляется при построении снимка списка, поэтому не попадает в settings.json и
    // автоматически обновляется после RefreshPlaylistView. Сам путь не удаляется молча: UI
    // показывает предупреждение и предлагает заменить запись или убрать только её из плейлиста.
    public bool IsFileAvailable => File.Exists(FilePath);
    public string MissingStatus => IsFileAvailable ? string.Empty : LocalizationService.Translate("Файл недоступен");
}
