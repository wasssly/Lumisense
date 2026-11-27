using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumisense;

// Звук для конкретного устройства вывода: эквалайзер и баланс. Профили лежат в settings.json по ID конечной точки Windows.
public class DeviceSoundProfile
{
    public string EndpointId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public bool EqualizerEnabled { get; set; }
    public double[] EqualizerBandGainsDb { get; set; } = new double[10];
    public int PlaybackBalance { get; set; }
}

internal static class DeviceSoundProfiles
{
    public const int MaxProfiles = 32;

    public static DeviceSoundProfile? Find(AppSettings settings, string endpointId) =>
        settings.DeviceSoundProfiles.FirstOrDefault(profile => string.Equals(profile.EndpointId, endpointId, StringComparison.Ordinal));

    // Записывает текущие эквалайзер и баланс в профиль устройства (создаёт его при необходимости); старые профили вытесняются сверх лимита.
    public static DeviceSoundProfile Capture(AppSettings settings, string endpointId, string deviceName)
    {
        var profile = Find(settings, endpointId);
        if (profile is null)
        {
            profile = new DeviceSoundProfile { EndpointId = endpointId };
            settings.DeviceSoundProfiles.Add(profile);
            while (settings.DeviceSoundProfiles.Count > MaxProfiles)
                settings.DeviceSoundProfiles.RemoveAt(0);
        }

        profile.DeviceName = deviceName;
        profile.EqualizerEnabled = settings.EqualizerEnabled;
        profile.EqualizerBandGainsDb = (double[])settings.EqualizerBandGainsDb.Clone();
        profile.PlaybackBalance = settings.PlaybackBalance;
        return profile;
    }

    // Переносит профиль в настройки; значения из файла ограничиваются допустимыми диапазонами.
    public static void Apply(AppSettings settings, DeviceSoundProfile profile)
    {
        int bands = EqualizerSampleProvider.BandFrequencies.Length;
        var gains = new double[bands];
        for (int band = 0; band < bands && band < profile.EqualizerBandGainsDb.Length; band++)
        {
            double gain = profile.EqualizerBandGainsDb[band];
            gains[band] = double.IsFinite(gain)
                ? Math.Clamp(gain, EqualizerSampleProvider.MinGainDb, EqualizerSampleProvider.MaxGainDb)
                : 0;
        }

        settings.EqualizerEnabled = profile.EqualizerEnabled;
        settings.EqualizerBandGainsDb = gains;
        settings.PlaybackBalance = Math.Clamp(profile.PlaybackBalance, -100, 100);
    }
}
