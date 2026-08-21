using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPlayer;

// Lumisense воспроизводит через WaveOutEvent, поэтому WaveOut-номер остаётся техническим
// идентификатором вывода. Однако WinMM ограничивает ProductName 32 символами; для интерфейса
// сопоставляем его с современными активными Windows render endpoints и показываем FriendlyName.
internal static class AudioOutputDeviceService
{
    // Пустая строка означает системный audio mapper Windows (WaveOut DeviceNumber = -1).
    public const string SystemDefaultDeviceName = "";
    public const int SystemDefaultDeviceNumber = -1;

    // DeviceName хранит legacy ProductName для обратной совместимости с WaveOutEvent, а
    // DisplayName — полный FriendlyName Core Audio, используемый только в интерфейсе.
    internal sealed record Option(int DeviceNumber, string DeviceName, string DisplayName);

    // NAudio 1.9 exposes WaveOutEvent but not the legacy WaveOut enumerator in the target used
    // by Lumisense. WinMM is the same Windows API behind WaveOutEvent, so we enumerate its
    // output devices directly while retaining NAudio's WaveOutCapabilities structure.
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto, ExactSpelling = false)]
    private static extern int waveOutGetDevCaps(IntPtr deviceId, out WaveOutCapabilities capabilities, int capabilitiesSize);

    public static IReadOnlyList<Option> GetAvailableDevices()
    {
        try
        {
            IReadOnlyList<string> fullEndpointNames = GetActiveRenderEndpointFriendlyNames();
            var devices = new List<Option>();
            var duplicateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            uint deviceCount = waveOutGetNumDevs();
            for (int deviceNumber = 0; deviceNumber < deviceCount; deviceNumber++)
            {
                if (waveOutGetDevCaps((IntPtr)deviceNumber, out WaveOutCapabilities capabilities, Marshal.SizeOf<WaveOutCapabilities>()) != 0)
                    continue;

                string legacyName = string.IsNullOrWhiteSpace(capabilities.ProductName)
                    ? $"WaveOut #{deviceNumber + 1}"
                    : capabilities.ProductName.Trim();
                string fullName = ResolveFullDisplayName(legacyName, fullEndpointNames);

                duplicateCounts.TryGetValue(fullName, out int duplicateIndex);
                duplicateCounts[fullName] = duplicateIndex + 1;
                string displayName = duplicateIndex == 0 ? fullName : $"{fullName} ({duplicateIndex + 1})";
                devices.Add(new Option(deviceNumber, legacyName, displayName));
            }

            return devices;
        }
        catch (Exception ex)
        {
            // Перечисление legacy WaveOut не должно мешать запуску плеера: audio mapper всё
            // ещё может работать, даже если драйвер одной из карт отдаёт ошибку capabilities.
            Logger.Warn($"Не удалось перечислить устройства вывода WaveOut: {ex.Message}");
            return Array.Empty<Option>();
        }
    }

    private static IReadOnlyList<string> GetActiveRenderEndpointFriendlyNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var names = new List<string>();
            foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(endpoint.FriendlyName))
                        names.Add(endpoint.FriendlyName.Trim());
                }
                finally
                {
                    endpoint.Dispose();
                }
            }

            return names;
        }
        catch (Exception ex)
        {
            // Core Audio используется только для полного отображаемого названия. Если он
            // недоступен, корректный, но короткий WinMM ProductName остаётся безопасным fallback.
            Logger.Warn($"Не удалось получить полные имена устройств Windows: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static string ResolveFullDisplayName(string legacyName, IReadOnlyList<string> endpointNames)
    {
        string normalizedLegacyName = NormalizeForMatch(legacyName);
        if (normalizedLegacyName.Length == 0) return legacyName;

        string? match = endpointNames
            .Select(name => new { Name = name, Normalized = NormalizeForMatch(name) })
            .Where(candidate => candidate.Normalized.StartsWith(normalizedLegacyName, StringComparison.Ordinal) ||
                                normalizedLegacyName.StartsWith(candidate.Normalized, StringComparison.Ordinal))
            .OrderBy(candidate => Math.Abs(candidate.Normalized.Length - normalizedLegacyName.Length))
            .Select(candidate => candidate.Name)
            .FirstOrDefault();

        return match ?? legacyName;
    }

    private static string NormalizeForMatch(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
                result.Append(char.ToUpperInvariant(character));
        }

        return result.ToString();
    }

    // Возвращает DeviceNumber, пригодный для WaveOutEvent. Если сохранённое устройство исчезло,
    // используем mapper Windows: он выберет текущее системное устройство и даёт лучший шанс
    // продолжить воспроизведение после отключения USB/Bluetooth-оборудования.
    public static int ResolveDeviceNumber(string? preferredDeviceName, out bool usedFallback)
    {
        if (string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            usedFallback = false;
            return SystemDefaultDeviceNumber;
        }

        Option? matched = GetAvailableDevices().FirstOrDefault(device =>
            string.Equals(device.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
        {
            usedFallback = false;
            return matched.DeviceNumber;
        }

        usedFallback = true;
        return SystemDefaultDeviceNumber;
    }

    public static bool IsAvailable(string? preferredDeviceName) =>
        string.IsNullOrWhiteSpace(preferredDeviceName) ||
        GetAvailableDevices().Any(device => string.Equals(device.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase));
}
