using System;
using System.Collections.Generic;

namespace Lumisense;

// Единые правила сравнения путей плейлиста. Windows различает регистр в тексте пути, но не в файловой системе,
// поэтому один файл, добавленный как C:\Music\a.mp3 и c:\music\A.mp3, должен считаться одним и тем же.
public static class PlaylistPaths
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static bool Equal(string? a, string? b) => Comparer.Equals(a, b);

    // Позиция пути в списке без учёта регистра; -1, если пути нет или он не задан.
    public static int IndexOf(IReadOnlyList<string> paths, string? path)
    {
        if (path is null) return -1;

        for (int i = 0; i < paths.Count; i++)
        {
            if (Comparer.Equals(paths[i], path)) return i;
        }

        return -1;
    }

    // Соседний доступный трек для случая, когда текущий файл пропал: его позиция берётся из полного списка (включая
    // недоступные), и от неё идём в нужную сторону (direction +1 или −1, по кругу) до первого доступного.
    // null — если текущего нет в списке или доступных треков вокруг нет.
    public static string? FindNeighborAvailable(
        IReadOnlyList<string> fullList, string currentPath, ISet<string> available, int direction)
    {
        int position = IndexOf(fullList, currentPath);
        int count = fullList.Count;
        if (position < 0 || count < 2 || direction == 0) return null;

        int step = direction > 0 ? 1 : -1;
        for (int offset = 1; offset < count; offset++)
        {
            int index = ((position + step * offset) % count + count) % count;
            if (available.Contains(fullList[index])) return fullList[index];
        }

        return null;
    }

    // Набор для быстрых проверок «уже есть» вместо List.Contains в цикле (O(n²) на больших плейлистах).
    public static HashSet<string> ToSet(IEnumerable<string> paths) => new(paths, Comparer);
}
