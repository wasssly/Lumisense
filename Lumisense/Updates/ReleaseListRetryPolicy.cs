namespace Lumisense;

// Решает, стоит ли повторять запрос списка релизов и как долго ждать. Лимит GitHub API без токена (60 запросов в час на адрес)
// отвечает 403 или 429: ждать минутами в окне настроек бессмысленно, а короткую паузу из Retry-After и сетевые сбои повторяем сразу.
internal static class ReleaseListRetryPolicy
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan MaxAutomaticWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] BackoffDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    public static TimeSpan DelayBeforeAttempt(int attemptIndex) =>
        attemptIndex <= 0 ? TimeSpan.Zero : BackoffDelays[Math.Min(attemptIndex, BackoffDelays.Length) - 1];

    // retryAfter — значение заголовка Retry-After, rateLimitRemaining — X-RateLimit-Remaining.
    public static bool ShouldRetry(int? httpStatus, TimeSpan? retryAfter, int? rateLimitRemaining)
    {
        if (httpStatus is null) return true; // сеть: DNS, таймаут, обрыв соединения

        if (httpStatus is 403 or 429)
        {
            // Лимит исчерпан и сброс не скоро: повтор сейчас ничего не даст.
            if (rateLimitRemaining == 0 && (retryAfter is null || retryAfter > MaxAutomaticWait)) return false;
            return retryAfter is { } wait && wait <= MaxAutomaticWait;
        }

        return httpStatus >= 500;
    }

    // Когда лимит сбросится, по заголовку X-RateLimit-Reset (секунды Unix).
    public static DateTimeOffset? ParseRateLimitReset(string? headerValue) =>
        long.TryParse(headerValue, out long seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
