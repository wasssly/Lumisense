using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lumisense;

// Значения AppSettings.ShuffleMode. "Deck" — прежнее поведение (ShuffleBagSelector), остальные режимы отличаются тем, как
// выбирается следующий трек.
public static class ShuffleModes
{
    public const string Deck = "Deck";
    public const string Random = "Random";
    public const string Favorites = "Favorites";
    public const string Rare = "Rare";
    public const string Folders = "Folders";

    public static readonly string[] All = { Deck, Random, Favorites, Rare, Folders };

    public static string Normalize(string? mode) =>
        mode is not null && Array.IndexOf(All, mode) >= 0 ? mode : Deck;
}

public static class ShuffleModeSelector
{
    // Вес избранного трека в режиме «Любимые чаще» относительно обычного (вес 1).
    internal const double FavoriteWeight = 3.0;

    // Нижняя граница веса в «Редкие чаще»: трек, который слушали десятки раз, всё равно иногда выпадает, а не исчезает навсегда.
    internal const double RareWeightFloor = 0.05;

    // bag нужен режимам «Колода» и «По папкам» (в последнем в колоде лежат первые треки ещё не сыгранных папок — реальные пути,
    // чтобы сохранение и восстановление колоды работали без изменений).
    public static string? TakeNext(
        string mode,
        IList<string> bag,
        IEnumerable<string> activeTracks,
        string? excludePath,
        Random random,
        Func<string, int>? playCount = null,
        Func<string, bool>? isFavorite = null,
        IReadOnlyList<string>? recent = null)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(activeTracks);
        ArgumentNullException.ThrowIfNull(random);

        switch (ShuffleModes.Normalize(mode))
        {
            case ShuffleModes.Random:
                return TakeWeighted(activeTracks, excludePath, random, _ => 1.0, recent);
            case ShuffleModes.Favorites:
                return TakeWeighted(activeTracks, excludePath, random,
                    path => isFavorite is not null && isFavorite(path) ? FavoriteWeight : 1.0, recent);
            case ShuffleModes.Rare:
                return TakeWeighted(activeTracks, excludePath, random,
                    path => Math.Max(RareWeightFloor, 1.0 / (1 + Math.Max(0, playCount?.Invoke(path) ?? 0))), recent);
            case ShuffleModes.Folders:
                return TakeFromFolders(bag, activeTracks, excludePath, random);
            default:
                return ShuffleBagSelector.TakeNext(bag, activeTracks, excludePath, random, FolderOf);
        }
    }

    // Случайный выбор с вероятностью, пропорциональной весу, умноженному на «усталость»: недавно игравшие треки и папки
    // получают меньший вес. Трек не повторяется подряд, если в плейлисте есть другие.
    private static string? TakeWeighted(
        IEnumerable<string> activeTracks, string? excludePath, Random random, Func<string, double> weight,
        IReadOnlyList<string>? recent = null)
    {
        List<string> active = Distinct(activeTracks);
        if (active.Count == 0) return null;
        if (active.Count == 1) return active[0];

        List<string> candidates = excludePath is null
            ? active
            : active.Where(path => !string.Equals(path, excludePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return active[0];

        var fatigue = Fatigue.Build(active, excludePath, recent);
        double total = 0;
        var weights = new double[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            double value = weight(candidates[i]) * fatigue.Factor(candidates[i]);
            weights[i] = double.IsFinite(value) && value > 0 ? value : 0;
            total += weights[i];
        }

        // Если «усталость» обнулила всех (крошечный плейлист), выбираем по базовым весам без неё.
        if (total <= 0)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                double value = weight(candidates[i]);
                weights[i] = double.IsFinite(value) && value > 0 ? value : 1.0;
                total += weights[i];
            }
        }

        double target = random.NextDouble() * total;
        for (int i = 0; i < candidates.Count; i++)
        {
            target -= weights[i];
            if (target < 0) return candidates[i];
        }

        return candidates[^1];
    }

    // Множитель веса по недавней истории. Последние HardWindow треков исключаются совсем, следующие до SoftWindow возвращаются
    // плавно; трек из той же папки, что и предыдущий, почти не выпадает, а из недавних папок выпадает вдвое реже.
    // Папки различают только при трёх и более группах: в плейлисте из одной папки это правило не имеет смысла.
    internal sealed class Fatigue
    {
        private const int RecentFolderCount = 3;
        private const double SameFolderFactor = 0.12, RecentFolderFactor = 0.5, MinRecoveredFactor = 0.15;

        private readonly Dictionary<string, int> _distance = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _recentFolders = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _previousFolder;
        private readonly int _hardWindow, _softWindow;
        private readonly bool _useFolders;

        private Fatigue(int hardWindow, int softWindow, bool useFolders, string? previousFolder)
        {
            _hardWindow = hardWindow;
            _softWindow = softWindow;
            _useFolders = useFolders;
            _previousFolder = previousFolder;
        }

        internal static Fatigue Build(List<string> active, string? excludePath, IReadOnlyList<string>? recent)
        {
            int count = active.Count;
            int hard = Math.Clamp(count / 5, 1, 8);
            int soft = Math.Clamp(count / 2, hard + 1, 30);
            int folders = active.Select(FolderOf).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).Count();
            var fatigue = new Fatigue(hard, soft, folders >= 3, excludePath is null ? null : FolderOf(excludePath));

            if (recent is null) return fatigue;

            int distance = 1;
            for (int i = recent.Count - 1; i >= 0 && distance <= soft; i--, distance++)
            {
                fatigue._distance.TryAdd(recent[i], distance);
                if (distance <= RecentFolderCount) fatigue._recentFolders.Add(FolderOf(recent[i]));
            }

            return fatigue;
        }

        internal double Factor(string path)
        {
            double factor = 1.0;
            if (_distance.TryGetValue(path, out int distance))
            {
                factor = distance <= _hardWindow
                    ? 0
                    : MinRecoveredFactor + (1 - MinRecoveredFactor) * (distance - _hardWindow) / (_softWindow - _hardWindow + 1.0);
            }

            if (_useFolders)
            {
                string folder = FolderOf(path);
                if (_previousFolder is not null && string.Equals(folder, _previousFolder, StringComparison.OrdinalIgnoreCase))
                    factor *= SameFolderFactor;
                else if (_recentFolders.Contains(folder))
                    factor *= RecentFolderFactor;
            }

            return factor;
        }
    }

    // Альбом играет подряд в порядке плейлиста, затем случайная ещё не сыгранная папка; пока не прошли все папки, они не повторяются.
    private static string? TakeFromFolders(IList<string> bag, IEnumerable<string> activeTracks, string? excludePath, Random random)
    {
        List<string> active = Distinct(activeTracks);
        if (active.Count == 0) return null;
        if (active.Count == 1) return active[0];

        string? currentFolder = excludePath is null ? null : FolderOf(excludePath);
        if (currentFolder is not null)
        {
            List<string> sameFolder = active.Where(path => FolderOf(path) == currentFolder).ToList();
            int position = sameFolder.FindIndex(path => string.Equals(path, excludePath, StringComparison.OrdinalIgnoreCase));
            if (position >= 0 && position + 1 < sameFolder.Count)
                return sameFolder[position + 1];
        }

        // Первый трек каждой папки в порядке плейлиста.
        var firstOfFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in active)
            firstOfFolder.TryAdd(FolderOf(path), path);

        if (firstOfFolder.Count == 1) return firstOfFolder.Values.First();

        var validEntries = new HashSet<string>(firstOfFolder.Values, StringComparer.OrdinalIgnoreCase);
        for (int i = bag.Count - 1; i >= 0; i--)
        {
            if (!validEntries.Contains(bag[i]) || FolderOf(bag[i]) == currentFolder)
                bag.RemoveAt(i);
        }

        if (bag.Count == 0)
        {
            foreach (var entry in firstOfFolder)
            {
                if (entry.Key != currentFolder) bag.Add(entry.Value);
            }

            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }
        }

        string next = bag[0];
        bag.RemoveAt(0);
        return next;
    }

    private static string FolderOf(string path) => Path.GetDirectoryName(path) ?? string.Empty;

    private static List<string> Distinct(IEnumerable<string> tracks) => tracks
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
