using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NAudio.CoreAudioApi;

namespace Lumisense;

/// <summary>
/// Перечисляет Windows Core Audio render endpoints для WASAPI и хранит их устойчивые endpoint-ID.
/// Старые ключи WaveOut (ProductName + occurrence index) по-прежнему распознаются, поэтому выбор
/// устройства из существующего settings.json мягко мигрирует при первом новом выборе пользователя.
/// </summary>
internal static class AudioOutputDeviceService
{
    public const string SystemDefaultDeviceName = "";

    private const string EndpointKeyPrefix = "wasapi:";
    private const char PersistKeySeparator = '\uE000';

    // DeviceNumber оставлен для совместимости с прежними settings/test-контрактами. WASAPI больше
    // не использует его для открытия устройства: устойчивым идентификатором служит EndpointId.
    internal sealed record Option(int DeviceNumber, string DeviceName, string DisplayName, int OccurrenceIndex,
        string? EndpointId = null);

    internal sealed record ResolvedEndpoint(MMDevice Device, string ActiveDeviceKey, bool UsedFallback);

    // Список устройств последнего удачного перечисления. Чтение FriendlyName у каждого endpoint — COM-вызовы, на некоторых
    // драйверах они занимают десятки миллисекунд, поэтому окно настроек показывает готовый список и обновляет его в фоне.
    private static IReadOnlyList<Option>? _cachedDevices;

    internal static IReadOnlyList<Option>? CachedDevices => Volatile.Read(ref _cachedDevices);

    // Устройства подключили или отключили: прежний список больше нельзя показывать как актуальный.
    internal static void InvalidateDeviceCache() => Volatile.Write(ref _cachedDevices, null);

    // Фоновое перечисление; null — не удалось (кэш при этом не меняется).
    internal static Task<IReadOnlyList<Option>?> TryGetAvailableDevicesAsync() => Task.Run(TryEnumerateDevices);

    public static IReadOnlyList<Option> GetAvailableDevices() => TryEnumerateDevices() ?? Array.Empty<Option>();

    private static IReadOnlyList<Option>? TryEnumerateDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = new List<Option>();
            var nameOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    string friendlyName = string.IsNullOrWhiteSpace(endpoint.FriendlyName)
                        ? "WASAPI output"
                        : endpoint.FriendlyName.Trim();
                    nameOccurrences.TryGetValue(friendlyName, out int occurrenceIndex);
                    nameOccurrences[friendlyName] = occurrenceIndex + 1;
                    string displayName = occurrenceIndex == 0 ? friendlyName : $"{friendlyName} ({occurrenceIndex + 1})";
                    devices.Add(new Option(-1, friendlyName, displayName, occurrenceIndex, endpoint.ID));
                }
                finally
                {
                    endpoint.Dispose();
                }
            }

            Volatile.Write(ref _cachedDevices, devices);
            return devices;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось перечислить WASAPI-устройства вывода: {ex.Message}");
            return null;
        }
    }

    public static string ComposePersistedKey(Option option)
    {
        if (!string.IsNullOrWhiteSpace(option.EndpointId))
            return $"{EndpointKeyPrefix}{option.EndpointId}";

        // Legacy fallback для старых тестов и повреждённых данных без endpoint-ID.
        return option.OccurrenceIndex == 0
            ? option.DeviceName
            : $"{option.DeviceName}{PersistKeySeparator}{option.OccurrenceIndex}";
    }

    internal static bool IsEndpointPersistedKey(string? persisted) =>
        !string.IsNullOrWhiteSpace(persisted) && persisted.StartsWith(EndpointKeyPrefix, StringComparison.Ordinal) &&
        persisted.Length > EndpointKeyPrefix.Length;

    internal static string? GetEndpointId(string? persisted) =>
        IsEndpointPersistedKey(persisted) ? persisted![EndpointKeyPrefix.Length..] : null;

    internal static (string Name, int? OccurrenceIndex) ParsePersistedKey(string persisted)
    {
        int separatorIndex = persisted.IndexOf(PersistKeySeparator);
        if (separatorIndex < 0) return (persisted, null);

        string name = persisted[..separatorIndex];
        string suffix = persisted[(separatorIndex + 1)..];
        return int.TryParse(suffix, out int occurrenceIndex) ? (name, occurrenceIndex) : (persisted, null);
    }

    /// <summary>
    /// Возвращает открытый Core Audio endpoint. Владение MMDevice передаётся вызывающему коду:
    /// он обязан Dispose endpoint после Dispose WasapiPlayer.
    /// </summary>
    public static ResolvedEndpoint ResolveEndpoint(string? preferredDeviceKey)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (string.IsNullOrWhiteSpace(preferredDeviceKey))
            return new ResolvedEndpoint(
                enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia),
                SystemDefaultDeviceName,
                UsedFallback: false);

        string? endpointId = GetEndpointId(preferredDeviceKey);
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            MMDevice? endpoint = TryGetActiveEndpointById(enumerator, endpointId);
            if (endpoint is not null)
                return new ResolvedEndpoint(endpoint, preferredDeviceKey, UsedFallback: false);
        }
        else
        {
            // Миграция ключей, сохранённых WaveOutEvent: сопоставляем старое ProductName (в том
            // числе усечённое WinMM до 32 символов) с современным FriendlyName endpoint.
            (string name, int? occurrenceIndex) = ParsePersistedKey(preferredDeviceKey);
            MMDevice? endpoint = TryGetActiveEndpointByLegacyName(enumerator, name, occurrenceIndex);
            if (endpoint is not null)
                return new ResolvedEndpoint(endpoint, ComposePersistedKey(new Option(-1,
                    endpoint.FriendlyName.Trim(), endpoint.FriendlyName.Trim(), 0, endpoint.ID)), UsedFallback: false);
        }

        return new ResolvedEndpoint(
            enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia),
            SystemDefaultDeviceName,
            UsedFallback: true);
    }

    /// <summary>
    /// Возвращает ключ текущего endpoint из списка доступных устройств, соответствующий
    /// сохранённому ключу. Нужен SettingsWindow, чтобы старый WaveOut-ключ не был ошибочно
    /// заменён системным устройством ещё до первой попытки воспроизведения.
    /// </summary>
    public static string? FindAvailablePersistedKey(string? preferredDeviceKey) =>
        FindAvailablePersistedKey(preferredDeviceKey, null);

    // devices — уже перечисленный список (окно настроек его только что получило): повторное перечисление не нужно.
    internal static string? FindAvailablePersistedKey(string? preferredDeviceKey, IReadOnlyList<Option>? devices)
    {
        if (string.IsNullOrWhiteSpace(preferredDeviceKey))
            return SystemDefaultDeviceName;

        devices ??= GetAvailableDevices();
        string? endpointId = GetEndpointId(preferredDeviceKey);
        Option? match;
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            match = devices.FirstOrDefault(device => string.Equals(device.EndpointId, endpointId, StringComparison.Ordinal));
        }
        else
        {
            (string name, int? occurrenceIndex) = ParsePersistedKey(preferredDeviceKey);
            match = FindLegacyNameMatch(devices, name, occurrenceIndex);
        }

        return match is null ? null : ComposePersistedKey(match);
    }

    public static bool IsAvailable(string? preferredDeviceKey) =>
        FindAvailablePersistedKey(preferredDeviceKey) is not null;

    public static string GetDisplayName(string? persistedDeviceKey)
    {
        if (string.IsNullOrWhiteSpace(persistedDeviceKey))
            return LocalizationService.Translate("Системное устройство по умолчанию");

        string? endpointId = GetEndpointId(persistedDeviceKey);
        Option? device = !string.IsNullOrWhiteSpace(endpointId)
            ? GetAvailableDevices().FirstOrDefault(option => string.Equals(option.EndpointId, endpointId, StringComparison.Ordinal))
            : FindLegacyDisplayOption(persistedDeviceKey);
        return device?.DisplayName ?? (!string.IsNullOrWhiteSpace(endpointId)
            ? LocalizationService.Translate("Недоступное WASAPI-устройство")
            : ParsePersistedKey(persistedDeviceKey).Name);
    }

    private static Option? FindLegacyDisplayOption(string persistedDeviceKey)
    {
        (string name, int? occurrenceIndex) = ParsePersistedKey(persistedDeviceKey);
        return FindLegacyNameMatch(GetAvailableDevices(), name, occurrenceIndex);
    }

    // Старый ключ хранит только имя: точное совпадение важнее совпадения по началу строки, иначе «Speakers» мог бы
    // выбрать «Speakers (USB)», если тот стоит в списке раньше.
    internal static Option? FindLegacyNameMatch(IReadOnlyList<Option> devices, string legacyName, int? occurrenceIndex)
    {
        List<Option> candidates = devices
            .Where(device => LegacyNameMatches(legacyName, device.DeviceName) &&
                (occurrenceIndex is null || occurrenceIndex == device.OccurrenceIndex))
            .ToList();

        string normalizedLegacy = NormalizeForMatch(legacyName);
        return candidates.FirstOrDefault(device => string.Equals(NormalizeForMatch(device.DeviceName), normalizedLegacy, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault();
    }

    private static MMDevice? TryGetActiveEndpointById(MMDeviceEnumerator enumerator, string endpointId)
    {
        foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (string.Equals(endpoint.ID, endpointId, StringComparison.Ordinal))
                return endpoint;
            endpoint.Dispose();
        }

        return null;
    }

    private static MMDevice? TryGetActiveEndpointByLegacyName(MMDeviceEnumerator enumerator, string legacyName,
        int? occurrenceIndex)
    {
        int matchingOccurrence = 0;
        foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            string friendlyName = endpoint.FriendlyName?.Trim() ?? string.Empty;
            if (!LegacyNameMatches(legacyName, friendlyName))
            {
                endpoint.Dispose();
                continue;
            }

            if (occurrenceIndex is null || matchingOccurrence == occurrenceIndex)
                return endpoint;

            matchingOccurrence++;
            endpoint.Dispose();
        }

        return null;
    }

    private static bool LegacyNameMatches(string legacyName, string friendlyName)
    {
        string normalizedLegacy = NormalizeForMatch(legacyName);
        string normalizedFriendly = NormalizeForMatch(friendlyName);
        return normalizedLegacy.Length > 0 && normalizedFriendly.Length > 0 &&
               (normalizedFriendly.StartsWith(normalizedLegacy, StringComparison.Ordinal) ||
                normalizedLegacy.StartsWith(normalizedFriendly, StringComparison.Ordinal));
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
}
