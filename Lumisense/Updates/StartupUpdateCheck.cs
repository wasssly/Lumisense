namespace Lumisense;

// Тихая проверка обновлений на старте с повторами: сеть при запуске часто ещё не готова (VPN, DNS, Wi-Fi), поэтому
// единичный сбой не должен значить «обновлений нет».
internal static class StartupUpdateCheck
{
    // Паузы перед повторами: вместе с начальной задержкой это попытки примерно на 3 с, 33 с и 5,5 мин после старта.
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    public static async Task<UpdateCheckResult> RunAsync(
        Func<CancellationToken, Task<UpdateCheckResult>> check,
        IReadOnlyList<TimeSpan> retryDelays,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        UpdateCheckResult result = await check(cancellationToken);
        foreach (TimeSpan wait in retryDelays)
        {
            if (!IsRetryable(result)) return result;

            await delay(wait, cancellationToken);
            result = await check(cancellationToken);
        }

        return result;
    }

    // Повторяем всё, что может быть временным; отсутствие контрольной суммы в релизе от повторов не исправится.
    internal static bool IsRetryable(UpdateCheckResult result) =>
        result.Status == UpdateCheckStatus.Error && result.FailureKind != UpdateFailureKind.MissingInstallerChecksum;
}
