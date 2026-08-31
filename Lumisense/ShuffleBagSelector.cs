using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumisense;

/// <summary>
/// Selects tracks from a shuffled bag. Every distinct active path is consumed once
/// before the bag is rebuilt, while the current path is excluded at cycle boundaries.
/// </summary>
public static class ShuffleBagSelector
{
    public static string? TakeNext(
        IList<string> bag,
        IEnumerable<string> activeTracks,
        string? excludePath,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(activeTracks);
        ArgumentNullException.ThrowIfNull(random);

        List<string> active = activeTracks
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (active.Count == 0) return null;
        if (active.Count == 1) return active[0];

        var activeSet = active.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = bag.Count - 1; i >= 0; i--)
        {
            if (!activeSet.Contains(bag[i])) bag.RemoveAt(i);
        }

        // Восстановленная колода может быть частичной и содержать только текущий трек.
        // Удаляем текущий путь из неё, чтобы он не повторился; если допустимых кандидатов
        // не осталось, строим новую полную колоду.
        if (excludePath is not null)
        {
            for (int i = bag.Count - 1; i >= 0; i--)
            {
                if (string.Equals(bag[i], excludePath, StringComparison.OrdinalIgnoreCase))
                    bag.RemoveAt(i);
            }
        }

        if (bag.Count == 0)
        {
            foreach (string path in active)
                bag.Add(path);
            ShuffleInPlace(bag, random);

            if (excludePath is not null && bag.Count > 1
                && string.Equals(bag[0], excludePath, StringComparison.OrdinalIgnoreCase))
            {
                int swapIndex = random.Next(1, bag.Count);
                (bag[0], bag[swapIndex]) = (bag[swapIndex], bag[0]);
            }
        }

        string next = bag[0];
        bag.RemoveAt(0);
        return next;
    }

    private static void ShuffleInPlace(IList<string> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
