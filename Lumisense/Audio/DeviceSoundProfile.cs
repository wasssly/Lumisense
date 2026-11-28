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
