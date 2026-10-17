using System.Globalization;
using System.Text.Json;

namespace Lumisense;

// Мелкие помощники для разбора ответов сторонних сервисов текстов: поля бывают то строкой, то числом, то отсутствуют.
internal static class OnlineLyricsJson
{
    public static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static string? GetFirstString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = GetString(element, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    public static double GetDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0;
    }

    public static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;
}
