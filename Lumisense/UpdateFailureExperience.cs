namespace AudioPlayer;

// UpdateChecker возвращает тип ошибки и безопасные параметры, но не локализованный текст.
// Это отделяет сетевой/JSON-слой от интерфейса и не позволяет техническим сообщениям GitHub
// случайно остаться на языке реализации в RU или EN окнах.
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
}
