using System.IO;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Lumisense;

// Разбор TTML (Better Lyrics, Binimum) в LRC по строкам; фоновые голоса и переводы пропускаются, DTD и внешние сущности запрещены.
internal static class TtmlLyricsParser
{
    private const int MaxCharacters = 2_000_000;

    private static readonly string[] SkippedRoles = { "x-bg", "x-translation", "x-roman" };

    public static (string? Synced, string? Plain) Parse(string? ttml)
    {
        if (string.IsNullOrWhiteSpace(ttml) || ttml.Length > MaxCharacters) return (null, null);

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxCharacters,
            };
            using var stringReader = new StringReader(ttml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return (null, null);
        }

        XElement? body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
        if (body is null) return (null, null);

        var timed = new List<(double Seconds, string Text)>();
        var plainLines = new List<string>();
        foreach (XElement paragraph in body.Descendants().Where(element => element.Name.LocalName == "p"))
        {
            string text = ReadText(paragraph);
            if (text.Length == 0) continue;

            plainLines.Add(text);
            double? begin = ParseTime(Attribute(paragraph, "begin")) ?? FirstSpanBegin(paragraph);
            if (begin is not null) timed.Add((begin.Value, text));
        }

        if (plainLines.Count == 0) return (null, null);

        // Без двух различимых меток времени (все нули или их нет совсем) LRC бесполезен: остаётся обычный текст.
        if (timed.Count >= 2 && timed.Select(line => Math.Round(line.Seconds, 2)).Distinct().Count() >= 2)
            return (BuildLrc(timed), null);

        return (null, string.Join('\n', plainLines));
    }

    internal static string BuildLrc(IEnumerable<(double Seconds, string Text)> lines)
    {
        var builder = new StringBuilder();
        foreach ((double seconds, string text) in lines.OrderBy(line => line.Seconds))
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            builder.Append('[')
                .Append(((int)time.TotalMinutes).ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append(time.Seconds.ToString("00", CultureInfo.InvariantCulture)).Append('.')
                .Append((time.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture))
                .Append(']').Append(text).Append('\n');
        }

        return builder.ToString().Trim();
    }

    // Слоги идут отдельными span без пробелов между ними, слова разделены пробельными узлами: текст собирается как есть,
    // а подряд идущие пробелы схлопываются.
    private static string ReadText(XElement paragraph)
    {
        var builder = new StringBuilder();
        AppendText(paragraph, builder);
        return CollapseWhitespace(builder.ToString());
    }

    private static void AppendText(XElement element, StringBuilder builder)
    {
        foreach (XNode node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement child when child.Name.LocalName == "br":
                    builder.Append(' ');
                    break;
                case XElement child when !IsSkipped(child):
                    AppendText(child, builder);
                    break;
            }
        }
    }

    private static bool IsSkipped(XElement element)
    {
        string? role = Attribute(element, "role");
        return role is not null && SkippedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char symbol in value)
        {
            if (char.IsWhiteSpace(symbol))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(symbol);
        }

        return builder.ToString();
    }

    private static double? FirstSpanBegin(XElement paragraph)
    {
        foreach (XElement span in paragraph.Descendants().Where(element => element.Name.LocalName == "span" && !IsSkipped(element)))
        {
            double? begin = ParseTime(Attribute(span, "begin"));
            if (begin is not null) return begin;
        }

        return null;
    }

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    // Форматы TTML: «12.345s», «1500ms», «01:02.500», «00:01:02.500», просто «12.5».
    internal static double? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string text = value.Trim();
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            return double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) ? ms / 1000 : null;
        if (text.EndsWith('s'))
            return double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? seconds : null;

        string[] parts = text.Split(':');
        if (parts.Length is < 1 or > 3) return null;

        double total = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < 0) return null;
            total = total * 60 + number;
        }

        return total;
    }
}
