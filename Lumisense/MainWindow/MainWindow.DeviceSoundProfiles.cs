using static Lumisense.BackgroundTask;

namespace Lumisense;

// Профили звука по устройствам: при смене выхода эквалайзер и баланс предыдущего устройства сохраняются, а нового применяются.
public partial class MainWindow
{
    // Вызывается при каждом открытии выхода; previousId — устройство, с которого переключились (null при первом запуске).
    private void SwitchDeviceSoundProfile(string? previousId, string previousName, string newId, string newName)
    {
        if (!_settings.DeviceSoundProfilesEnabled || string.Equals(previousId, newId, StringComparison.Ordinal)) return;

        if (previousId is not null)
            DeviceSoundProfiles.Capture(_settings, previousId, previousName);

        var profile = DeviceSoundProfiles.Find(_settings, newId);
        if (profile is null)
        {
            // Новое устройство наследует текущую настройку, дальше она живёт отдельно.
            DeviceSoundProfiles.Capture(_settings, newId, newName);
            return;
        }

        DeviceSoundProfiles.Apply(_settings, profile);
        ApplyEqualizerGainsFromSettings();
        ApplyChannelMode();
        _settingsWindow?.RefreshSoundProfileControls();
        FireAndForget(SettingsManager.SaveAsync(_settings), "SaveSettingsAsync");
    }

    // Сохраняет настройку текущего устройства (при выходе и при включении режима).
    private void CaptureCurrentDeviceSoundProfile()
    {
        if (!_settings.DeviceSoundProfilesEnabled || string.IsNullOrEmpty(_outputDeviceId)) return;

        DeviceSoundProfiles.Capture(_settings, _outputDeviceId, _outputDeviceName ?? "");
    }

    public bool IsDeviceSoundProfilesEnabled => _settings.DeviceSoundProfilesEnabled;

    public void SetDeviceSoundProfilesEnabled(bool enabled)
    {
        _settings.DeviceSoundProfilesEnabled = enabled;
        CaptureCurrentDeviceSoundProfile();
    }
}
