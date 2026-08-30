using System;
using NAudio.CoreAudioApi;

namespace AudioPlayer;

/// <summary>
/// Isolates endpoint selection decisions from MainWindow.
/// It does not own the returned MMDevice; ownership is transferred to the caller.
/// </summary>
internal sealed class AudioOutputDeviceManager
{
    public AudioOutputDeviceService.ResolvedEndpoint Resolve(string? preferredDeviceKey) =>
        AudioOutputDeviceService.ResolveEndpoint(preferredDeviceKey);

    public static string? GetFallbackSourceKey(string? requestedDeviceKey, bool usedFallback) =>
        usedFallback && !string.IsNullOrWhiteSpace(requestedDeviceKey)
            ? requestedDeviceKey
            : null;

    public static bool ShouldPersistActiveKey(string? requestedDeviceKey, string? activeDeviceKey, bool usedFallback)
    {
        if (usedFallback || string.IsNullOrWhiteSpace(activeDeviceKey))
            return false;

        return !string.Equals(requestedDeviceKey, activeDeviceKey, StringComparison.Ordinal);
    }

    public static bool ShouldRestoreSavedEndpoint(
        string? savedDeviceKey,
        string? activeDeviceKey,
        bool outputIsFallback) =>
        outputIsFallback && !string.IsNullOrWhiteSpace(savedDeviceKey) &&
        !string.Equals(savedDeviceKey, activeDeviceKey, StringComparison.Ordinal);
}
