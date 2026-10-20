using System.Collections.Generic;
using System.Linq;

namespace Lumisense;

// Помнит, о каких пропавших файлах пользователя уже уведомили, чтобы каждое обновление папки не повторяло одно и то же:
// сообщать нужно только о файлах, которые стали недоступны с прошлого раза. Вернувшийся файл «забывается», и если он
// пропадёт снова, об этом уведомят повторно.
public sealed class UnavailableFilesTracker
{
    private readonly HashSet<string> _announced = new(PlaylistPaths.Comparer);

    // trackedPaths — все пути проверенной папки, missing — те из них, что сейчас недоступны.
    // Возвращает недоступные пути, о которых ещё не сообщали (в порядке missing).
    public List<string> Update(IEnumerable<string> trackedPaths, IEnumerable<string> missing)
    {
        var tracked = PlaylistPaths.ToSet(trackedPaths);
        var missingList = missing.ToList();
        var missingSet = PlaylistPaths.ToSet(missingList);

        _announced.RemoveWhere(path => tracked.Contains(path) && !missingSet.Contains(path));
        return missingList.Where(path => _announced.Add(path)).ToList();
    }
}
