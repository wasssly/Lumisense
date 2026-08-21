using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AudioPlayer;

// Минимальная реализация SemVer 2.0 для локальной проверки обновлений. System.Version не
// поддерживает prerelease/metadata, а строковое сравнение могло предлагать ложное обновление.
internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        Match match = Pattern.Match(value.Trim());
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
            return false;

        string? preRelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (preRelease?.Split('.').Any(part => part.Length > 1 && part[0] == '0' && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) == true)
            return false;

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        int numeric = Major.CompareTo(other.Major);
        if (numeric != 0) return numeric;
        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0) return numeric;
        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;

        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;

        string[] left = PreRelease.Split('.');
        string[] right = other.PreRelease.Split('.');
        int shared = Math.Min(left.Length, right.Length);
        for (int index = 0; index < shared; index++)
        {
            bool leftIsNumber = int.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
            bool rightIsNumber = int.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);

            if (leftIsNumber && rightIsNumber)
            {
                int comparison = leftNumber.CompareTo(rightNumber);
                if (comparison != 0) return comparison;
                continue;
            }

            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;
            int textComparison = string.CompareOrdinal(left[index], right[index]);
            if (textComparison != 0) return textComparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => PreRelease is null
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
