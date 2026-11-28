using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumisense;

internal enum ParametricFilterType
{
    Peaking,
    LowShelf,
    HighShelf
}

internal readonly record struct ParametricFilter(ParametricFilterType Type, double FrequencyHz, double GainDb, double Q);

// Профиль AutoEq / Equalizer APO: строки "Preamp: -6.2 dB" и "Filter 1: ON PK Fc 105 Hz Gain 2.5 dB Q 0.70".
internal sealed class ParametricEqProfile
{
    public const int MaxFilters = 64;
    public const long MaxFileBytes = 256 * 1024;

    private const double DefaultQ = 0.707;

    private static readonly Regex FilterLine = new(
        @"^\s*Filter\s*\d*\s*:\s*(?<state>ON|OFF)\s+(?<type>[A-Z]+)(?:\s+\d+\s*dB)?\s+Fc\s+(?<fc>[\d.,]+)\s*Hz" +
        @"(?:\s+Gain\s+(?<gain>[-+]?[\d.,]+)\s*dB)?(?:\s+Q\s+(?<q>[\d.,]+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PreampLine = new(
        @"^\s*Preamp\s*:\s*(?<gain>[-+]?[\d.,]+)\s*dB",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public double PreampDb { get; }
    public IReadOnlyList<ParametricFilter> Filters { get; }

    private ParametricEqProfile(double preampDb, IReadOnlyList<ParametricFilter> filters)
    {
        PreampDb = preampDb;
        Filters = filters;
    }

    // null, если в тексте нет ни одного поддерживаемого включённого фильтра (HP/LP/notch и прочее пропускаются).
    public static ParametricEqProfile? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        double preamp = 0;
        var filters = new List<ParametricFilter>();
        foreach (string line in text.Split('\n'))
        {
            Match preampMatch = PreampLine.Match(line);
            if (preampMatch.Success)
            {
                if (TryNumber(preampMatch.Groups["gain"].Value, out double value))
                    preamp = Math.Clamp(value, -30, 12);
                continue;
            }

            Match match = FilterLine.Match(line);
            if (!match.Success || !match.Groups["state"].Value.Equals("ON", StringComparison.OrdinalIgnoreCase)) continue;

            ParametricFilterType? type = match.Groups["type"].Value.ToUpperInvariant() switch
            {
                "PK" or "PEQ" => ParametricFilterType.Peaking,
                "LSC" or "LS" => ParametricFilterType.LowShelf,
                "HSC" or "HS" => ParametricFilterType.HighShelf,
                _ => null
            };
            if (type is null || !TryNumber(match.Groups["fc"].Value, out double frequency)) continue;

            double gain = match.Groups["gain"].Success && TryNumber(match.Groups["gain"].Value, out double g) ? g : 0;
            double q = match.Groups["q"].Success && TryNumber(match.Groups["q"].Value, out double qValue) ? qValue : DefaultQ;
            if (frequency < 10 || frequency > 24000 || q <= 0) continue;

            filters.Add(new ParametricFilter(type.Value, frequency, Math.Clamp(gain, -30, 30), Math.Clamp(q, 0.1, 20)));
            if (filters.Count >= MaxFilters) break;
        }

        return filters.Count == 0 ? null : new ParametricEqProfile(preamp, filters);
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
