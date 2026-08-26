using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPlayer;

internal enum AudioOutputEndpointChangeKind
{
    DeviceAdded,
    DeviceRemoved,
    DeviceStateChanged,
    DefaultDeviceChanged,
    DevicePropertiesChanged
}

internal sealed class AudioOutputEndpointChangedEventArgs : EventArgs
{
    public AudioOutputEndpointChangedEventArgs(AudioOutputEndpointChangeKind kind, string? endpointId,
        DeviceState? state = null)
    {
        Kind = kind;
        EndpointId = endpointId;
        State = state;
    }

    public AudioOutputEndpointChangeKind Kind { get; }
    public string? EndpointId { get; }
    public DeviceState? State { get; }
}

/// <summary>
/// Подписывается на Core Audio endpoint events. Callback приходит с системного COM-потока,
/// поэтому потребитель обязан самостоятельно переходить на UI Dispatcher.
/// </summary>
internal sealed class AudioOutputEndpointMonitor : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private int _disposed;

    public AudioOutputEndpointMonitor()
    {
        int result = _enumerator.RegisterEndpointNotificationCallback(this);
        if (result != 0)
            Logger.Warn($"Не удалось подписаться на события WASAPI-устройств: HRESULT 0x{result:X8}");
    }

    public event EventHandler<AudioOutputEndpointChangedEventArgs>? EndpointChanged;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Raise(AudioOutputEndpointChangeKind.DeviceStateChanged, deviceId, newState);

    public void OnDeviceAdded(string pwstrDeviceId) =>
        Raise(AudioOutputEndpointChangeKind.DeviceAdded, pwstrDeviceId);

    public void OnDeviceRemoved(string deviceId) =>
        Raise(AudioOutputEndpointChangeKind.DeviceRemoved, deviceId);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            Raise(AudioOutputEndpointChangeKind.DefaultDeviceChanged, defaultDeviceId);
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
        Raise(AudioOutputEndpointChangeKind.DevicePropertiesChanged, pwstrDeviceId);

    private void Raise(AudioOutputEndpointChangeKind kind, string? endpointId, DeviceState? state = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            EndpointChanged?.Invoke(this, new AudioOutputEndpointChangedEventArgs(kind, endpointId, state));
        }
        catch (Exception ex)
        {
            // COM callback не должен выбрасывать исключение в Windows Audio service thread.
            Logger.Error("Ошибка обработки события WASAPI-устройства", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось отписаться от событий WASAPI-устройств: {ex.Message}");
        }
        finally
        {
            _enumerator.Dispose();
        }
    }
}
