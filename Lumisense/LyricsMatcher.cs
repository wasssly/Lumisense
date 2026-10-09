using System.Text.RegularExpressions;

namespace Lumisense;

// Сопоставление найденных онлайн-текстов с текущим треком. Раньше автоподбор требовал полного совпадения названия и
// исполнителя после удаления знаков, поэтому «Song (feat. X) - Remastered 2011» не находил «Song», а «A & B» не находил «A».
// Теперь названия очищаются от пометок версии и гостей, исполнители сравниваются по пересечению, а длительность
// отсекает другие версии трека. Совпадение по-прежнему обязано быть уверенным: сомнительное оставляем на ручной выбор.
public static class LyricsMatcher
{
    private const double StrongDurationToleranceSeconds = 2.5;
    private const double MaxDurationDifferenceSeconds = 8;

    private const string VersionWords =
        "feat|ft|featuring|with|live|remaster|remastered|remix|version|edit|mix|acoustic|deluxe|bonus|mono|stereo|explicit|radio|single|album|original|demo|ost|soundtrack";

    private static readonly Regex BracketedNoise = new(
        @"\s*[\(\[\{（【][^\)\]\}）】]*\b(?:" + VersionWords + @")\b[^\)\]\}）】]*[\)\]\}）】]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DashSuffixNoise = new(
        @"\s+[-–—]\s+[^-–—]*\b(?:remaster|remastered|live|version|edit|mix|remix|mono|stereo|single|radio|acoustic|deluxe)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingFeaturing = new(
        @"\s+(?:feat|ft|featuring)\.?\s+.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArtistSplitter = new(
        @"\s*(?:;|,|/|\s+feat\.?\s+|\s+ft\.?\s+|\s+featuring\s+)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Сравнительный ключ: только буквы и цифры в нижнем регистре; работает и для кириллицы, и для иероглифов.
    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    // Название без пометок вроде «(feat. X)», «(Remastered 2011)», «- Live», «- Radio Edit». Если после очистки ничего
    // не осталось, возвращается исходное название.
    public static string CleanTitle(string? title)
    {
        string source = title?.Trim() ?? string.Empty;
        if (source.Length == 0) return source;

        string cleaned = BracketedNoise.Replace(source, string.Empty);
        cleaned = DashSuffixNoise.Replace(cleaned, string.Empty);
        cleaned = TrailingFeaturing.Replace(cleaned, string.Empty).Trim();
        return cleaned.Length == 0 ? source : cleaned;
    }

    // Исполнители из тега: разделители «;», «,», «/», «feat.». «&» не режем: «Simon & Garfunkel» — один исполнитель.
    public static IReadOnlyList<string> SplitArtists(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist) || artist.Trim() == "—") return Array.Empty<string>();
        return ArtistSplitter.Split(artist)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    public static string FirstArtist(string? artist) => SplitArtists(artist).FirstOrDefault() ?? string.Empty;

    // Варианты запроса от самого точного к самому мягкому, без повторов. Каждый вариант — пара (название, исполнитель).
    public static IReadOnlyList<(string Title, string Artist)> BuildQueryVariants(string? title, string? artist)
    {
        string rawTitle = title?.Trim() ?? string.Empty;
        string rawArtist = artist?.Trim() ?? string.Empty;
        if (rawArtist == "—") rawArtist = string.Empty;

        string cleanTitle = CleanTitle(rawTitle);
        string firstArtist = FirstArtist(rawArtist);

        var variants = new List<(string, string)>
        {
            (rawTitle, rawArtist),
            (cleanTitle, rawArtist),
            (cleanTitle, firstArtist),
            (cleanTitle, string.Empty),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string Title, string Artist)>();
        foreach ((string variantTitle, string variantArtist) in variants)
        {
            if (string.IsNullOrWhiteSpace(variantTitle)) continue;
            if (seen.Add(variantTitle + "\u0001" + variantArtist)) result.Add((variantTitle, variantArtist));
        }

        return result;
    }

    // Лучший уверенный кандидат или null. Название (после очистки) должно совпасть; исполнитель — пересечься, если он известен;
    // длительность, если известна с обеих сторон, не должна отличаться больше чем на 8 секунд.
    public static OnlineLyricsResult? FindBest(
        IReadOnlyList<OnlineLyricsResult> results,
        string? title,
        string? artist,
        double durationSeconds)
    {
        string wantedTitle = NormalizeKey(CleanTitle(title));
        if (wantedTitle.Length == 0) return null;

        string rawTitleKey = NormalizeKey(title);
        HashSet<string> wantedArtists = ArtistKeys(artist);

        OnlineLyricsResult? best = null;
        double bestScore = double.MinValue;
        foreach (OnlineLyricsResult result in results)
        {
            if (!result.HasLyrics) continue;
            if (NormalizeKey(CleanTitle(result.TrackName)) != wantedTitle) continue;

            if (wantedArtists.Count > 0 && !ArtistsOverlap(wantedArtists, ArtistKeys(result.ArtistName))) continue;

            double score = 0;
            if (durationSeconds > 0 && result.Duration > 0)
            {
                double difference = Math.Abs(durationSeconds - result.Duration);
                if (difference > MaxDurationDifferenceSeconds) continue;
                score += difference <= StrongDurationToleranceSeconds ? 2 : 0.5;
            }

            if (result.HasSyncedLyrics) score += 2;
            if (NormalizeKey(result.TrackName) == rawTitleKey) score += 1;
            if (result.Source == "LRCLIB") score += 0.25;

            // Строго «больше»: при равенстве остаётся более ранний результат, то есть LRCLIB раньше дополнительных источников.
            if (score > bestScore)
            {
                best = result;
                bestScore = score;
            }
        }

        return best;
    }

    private static HashSet<string> ArtistKeys(string? artist)
    {
        var keys = new HashSet<string>();
        string full = NormalizeKey(artist);
        if (full.Length > 0) keys.Add(full);

        foreach (string part in SplitArtists(artist))
        {
            string key = NormalizeKey(part);
            if (key.Length > 0) keys.Add(key);

            // Внутри одного поля «A & B» исполнители считаются отдельно, чтобы «A» находил «A & B» и наоборот.
            foreach (string sub in part.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string subKey = NormalizeKey(sub);
                if (subKey.Length > 0) keys.Add(subKey);
            }
        }

        return keys;
    }

    private static bool ArtistsOverlap(HashSet<string> wanted, HashSet<string> candidate)
    {
        foreach (string left in wanted)
        {
            foreach (string right in candidate)
            {
                if (left == right) return true;
                // Вложение допускаем только для достаточно длинных ключей, чтобы «Ed» не совпадал с «Edguy».
                if (left.Length >= 4 && right.Length >= 4 && (left.Contains(right) || right.Contains(left))) return true;
            }
        }

        return false;
    }
}
