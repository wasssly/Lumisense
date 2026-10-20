namespace Lumisense;

// UpdateChecker возвращает тип ошибки, а не готовый текст — отделяет сетевой слой от UI
// и не даёт техническим сообщениям GitHub протечь в RU/EN окна.
internal static class UpdateFailureExperience
{
    public static string Describe(UpdateFailureKind kind, int? httpStatusCode = null) => kind switch
    {
        UpdateFailureKind.HttpStatus => LocalizationService.FormatKey(
            LocalizationKey.UpdateFailureHttpStatus, httpStatusCode ?? 0),
        UpdateFailureKind.InvalidResponse => LocalizationService.Get(
            LocalizationKey.UpdateFailureInvalidResponse),
        UpdateFailureKind.MissingInstallerChecksum => LocalizationService.Get(
            LocalizationKey.UpdateFailureMissingInstallerChecksum),
        UpdateFailureKind.Network => LocalizationService.Get(
            LocalizationKey.UpdateFailureNetwork),
        _ => LocalizationService.Get(LocalizationKey.UpdateFailureGeneric)
    };

    public static string DescribeVersionListFailure(ReleaseListResult result) =>
        LocalizationService.FormatKey(LocalizationKey.UpdateFailureLoadVersionList,
            Describe(result.FailureKind, result.HttpStatusCode));

    // Код 403 или 429 от GitHub API почти всегда лимит запросов без токена (60 в час на адрес), а не запрет: объясняем это и
    // говорим, когда лимит сбросится, вместо голого «GitHub вернул код 403».
    public static bool IsRateLimit(UpdateFailureKind kind, int? httpStatusCode) =>
        kind == UpdateFailureKind.HttpStatus && httpStatusCode is 403 or 429;

    public static string DescribeRateLimit(int httpStatusCode, DateTimeOffset? resetAt)
    {
        string message = string.Format(
            LocalizationService.Translate("GitHub ограничил число запросов с вашего адреса (код {0}), обычно это проходит само."),
            httpStatusCode);
        if (resetAt is { } reset && reset > DateTimeOffset.UtcNow)
            message += " " + string.Format(
                LocalizationService.Translate("Лимит сбросится около {0}."), reset.ToLocalTime().ToString("t"));
        return message + " " + LocalizationService.Translate("Нажмите «Повторить» позже.");
    }
}
