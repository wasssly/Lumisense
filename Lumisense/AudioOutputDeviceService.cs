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
    // OccurrenceIndex — позиция среди устройств с совпадающим DeviceName (0 для первого) —
    // без неё выбор между двумя одинаково названными устройствами было бы невозможно сохранить,
    // так как DeviceName у них буквально совпадает (см. ComposePersistedKey/ResolveDeviceNumber).
    internal sealed record Option(int DeviceNumber, string DeviceName, string DisplayName, int OccurrenceIndex);

    // Разделитель имени и OccurrenceIndex в сохранённом значении — символ из Private Use Area,
    // который не встречается в реальных строках драйверов Windows.
    private const char PersistKeySeparator = '\uE000';

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
            var displayNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var legacyNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            uint deviceCount = waveOutGetNumDevs();
            for (int deviceNumber = 0; deviceNumber < deviceCount; deviceNumber++)
            {
                if (waveOutGetDevCaps((IntPtr)deviceNumber, out WaveOutCapabilities capabilities, Marshal.SizeOf<WaveOutCapabilities>()) != 0)
                    continue;

                string legacyName = string.IsNullOrWhiteSpace(capabilities.ProductName)
                    ? $"WaveOut #{deviceNumber + 1}"
                    : capabilities.ProductName.Trim();
                string fullName = ResolveFullDisplayName(legacyName, fullEndpointNames);

                displayNameCounts.TryGetValue(fullName, out int displayDuplicateIndex);
                displayNameCounts[fullName] = displayDuplicateIndex + 1;
                string displayName = displayDuplicateIndex == 0 ? fullName : $"{fullName} ({displayDuplicateIndex + 1})";

                legacyNameCounts.TryGetValue(legacyName, out int occurrenceIndex);
                legacyNameCounts[legacyName] = occurrenceIndex + 1;

                devices.Add(new Option(deviceNumber, legacyName, displayName, occurrenceIndex));
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

    // Ключ для persistence: только имя для первого устройства с таким именем (обычный случай,
    // без дублей — формат не меняется относительно того, что было раньше), и "имя+индекс" для
    // второго и далее — иначе выбор между ними в настройках было бы неразличим.
    public static string ComposePersistedKey(Option option) =>
        option.OccurrenceIndex == 0
            ? option.DeviceName
            : $"{option.DeviceName}{PersistKeySeparator}{option.OccurrenceIndex}";

    internal static (string Name, int? OccurrenceIndex) ParsePersistedKey(string persisted)
    {
        int separatorIndex = persisted.IndexOf(PersistKeySeparator);
        if (separatorIndex < 0) return (persisted, null);

        string name = persisted[..separatorIndex];
        string suffix = persisted[(separatorIndex + 1)..];
        return int.TryParse(suffix, out int occurrenceIndex) ? (name, occurrenceIndex) : (persisted, null);
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

        (string name, int? occurrenceIndex) = ParsePersistedKey(preferredDeviceName);
        List<Option> sameNameDevices = GetAvailableDevices()
            .Where(device => string.Equals(device.DeviceName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Точное совпадение по имени и позиции среди одноимённых устройств — то, что было
        // реально выбрано. Если этой конкретной позиции больше нет (устройство отключили),
        // откатываемся на первое совпадение по имени — как и раньше, до появления OccurrenceIndex.
        Option? matched = (occurrenceIndex is int index
            ? sameNameDevices.FirstOrDefault(device => device.OccurrenceIndex == index)
            : null) ?? sameNameDevices.FirstOrDefault();

        if (matched is not null)
        {
            usedFallback = false;
            return matched.DeviceNumber;
        }

        usedFallback = true;
        return SystemDefaultDeviceNumber;
    }

    public static bool IsAvailable(string? preferredDeviceName)
    {
        if (string.IsNullOrWhiteSpace(preferredDeviceName)) return true;
        string name = ParsePersistedKey(preferredDeviceName).Name;
        return GetAvailableDevices().Any(device => string.Equals(device.DeviceName, name, StringComparison.OrdinalIgnoreCase));
    }
}
