namespace AudioPlayer;

/// <summary>
/// Чистые правила, по которым Lumisense решает, нужно ли пересоздать fixed-endpoint
/// WasapiPlayer после системного события. Сами COM callbacks остаются в monitor короткими.
/// </summary>
internal static class AudioOutputRecoveryPolicy
{
    public static bool FollowsSystemDefault(string? persistedDeviceKey) =>
        string.IsNullOrWhiteSpace(persistedDeviceKey);

    public static bool ShouldRecoverAfterDefaultDeviceChanged(string? persistedDeviceKey,
        string? activeEndpointId, string? changedDefaultEndpointId) =>
        FollowsSystemDefault(persistedDeviceKey) &&
        (string.IsNullOrWhiteSpace(activeEndpointId) ||
         string.IsNullOrWhiteSpace(changedDefaultEndpointId) ||
         !string.Equals(activeEndpointId, changedDefaultEndpointId, StringComparison.Ordinal));
}
