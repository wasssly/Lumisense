using System.IO;

namespace Lumisense;

// Строка трека для единого виртуализируемого ListView (и плейлист, и "Избранное").
// Раньше каждая папка рендерилась вложенным ListView без виртуализации — тормозило запуск.
public sealed class PlaylistTrackRow
{
    public required PlaylistFolder Folder { get; init; }
    public required string FilePath { get; init; }

    // 1-based номер трека внутри своей папки, считается заранее при построении списка
    public required int IndexInFolder { get; init; }

    // Статус считается при построении снимка списка и не попадает в settings.json.
    // Путь не удаляется молча: UI предупреждает и предлагает заменить запись или убрать её.
    public bool IsFileAvailable => File.Exists(FilePath);
    public string MissingStatus => IsFileAvailable ? string.Empty : LocalizationService.Translate("Файл недоступен");
}
