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
}
