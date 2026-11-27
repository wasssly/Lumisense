using System.Globalization;

namespace Lumisense;

// Учёт здоровья проверки обновлений: если она долго не удаётся (например, GitHub недоступен), один раз в сутки мягко
// подсказываем, где взять новую версию вручную. Метки времени лежат в настройках строками ISO 8601.
internal static class UpdateCheckHealth
{
    public const string TelegramChannelUrl = "https://t.me/LumisenseApp";
    public const string TelegramChannelName = "@LumisenseApp";

    public static readonly TimeSpan FailingFor = TimeSpan.FromDays(7);
    public static readonly TimeSpan HintCooldown = TimeSpan.FromDays(1);

    public static string Format(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static DateTime? Parse(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed.ToUniversalTime()
            : null;

    // failingSince — когда проверки начали подряд не удаваться (сбрасывается первой успешной).
    public static bool ShouldShowFailureHint(DateTime nowUtc, DateTime? failingSinceUtc, DateTime? lastHintUtc)
    {
        if (failingSinceUtc is not { } failingSince || nowUtc - failingSince < FailingFor) return false;
        return lastHintUtc is not { } lastHint || nowUtc - lastHint >= HintCooldown;
    }
}
