using System;
using System.Threading;
using NAudio.CoreAudioApi;

namespace Lumisense;

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
/// Подписывается на Core Audio endpoint events через публичный event API NAudio 3.
/// Обработчики остаются короткими и передают дальнейшую работу потребителю, который сам
/// переходит на WPF Dispatcher.
/// </summary>
internal sealed class AudioOutputEndpointMonitor : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private int _disposed;

    public AudioOutputEndpointMonitor()
    {
        // MainWindow уже маршрутизирует событие через Dispatcher.BeginInvoke. Не захватываем
        // SynchronizationContext здесь, чтобы Core Audio callback оставался неблокирующим.
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DeviceAdded += Notifications_DeviceAdded;
        _notifications.DeviceRemoved += Notifications_DeviceRemoved;
        _notifications.DeviceStateChanged += Notifications_DeviceStateChanged;
        _notifications.DefaultDeviceChanged += Notifications_DefaultDeviceChanged;
        _notifications.PropertyValueChanged += Notifications_PropertyValueChanged;
    }

    public event EventHandler<AudioOutputEndpointChangedEventArgs>? EndpointChanged;

    private void Notifications_DeviceAdded(object? sender, DeviceNotificationEventArgs e) =>
        Raise(AudioOutputEndpointChangeKind.DeviceAdded, e.DeviceId);

    private void Notifications_DeviceRemoved(object? sender, DeviceNotificationEventArgs e) =>
        Raise(AudioOutputEndpointChangeKind.DeviceRemoved, e.DeviceId);

    private void Notifications_DeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) =>
        Raise(AudioOutputEndpointChangeKind.DeviceStateChanged, e.DeviceId, e.NewState);

    private void Notifications_DefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
            Raise(AudioOutputEndpointChangeKind.DefaultDeviceChanged, e.DeviceId);
    }

    private void Notifications_PropertyValueChanged(object? sender, DevicePropertyChangedEventArgs e) =>
        Raise(AudioOutputEndpointChangeKind.DevicePropertiesChanged, e.DeviceId);

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
            // Callback не должен выбрасывать исключение в Windows Audio service thread.
            Logger.Error("Ошибка обработки события WASAPI-устройства", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _notifications.DeviceAdded -= Notifications_DeviceAdded;
        _notifications.DeviceRemoved -= Notifications_DeviceRemoved;
        _notifications.DeviceStateChanged -= Notifications_DeviceStateChanged;
        _notifications.DefaultDeviceChanged -= Notifications_DefaultDeviceChanged;
        _notifications.PropertyValueChanged -= Notifications_PropertyValueChanged;
        _notifications.Dispose();
        _enumerator.Dispose();
    }
}
