using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioOutputDeviceServiceTests
{
    [Fact]
    public void ComposePersistedKey_FirstOccurrence_ReturnsBareName()
    {
        var option = new AudioOutputDeviceService.Option(0, "USB Audio DAC", "USB Audio DAC", OccurrenceIndex: 0);

        string key = AudioOutputDeviceService.ComposePersistedKey(option);

        Assert.Equal("USB Audio DAC", key);
    }

    [Fact]
    public void ComposePersistedKey_SecondOccurrence_IncludesIndexSuffix()
    {
        var option = new AudioOutputDeviceService.Option(1, "USB Audio DAC", "USB Audio DAC (2)", OccurrenceIndex: 1);

        string key = AudioOutputDeviceService.ComposePersistedKey(option);

        Assert.NotEqual("USB Audio DAC", key);
        Assert.StartsWith("USB Audio DAC", key);
    }

    [Fact]
    public void ParsePersistedKey_BareNameWithoutSuffix_ReturnsNullOccurrenceIndex()
    {
        // Формат, в котором хранились значения до появления OccurrenceIndex — должен
        // продолжать разбираться корректно (обратная совместимость со старыми settings.json).
        (string name, int? occurrenceIndex) = AudioOutputDeviceService.ParsePersistedKey("USB Audio DAC");

        Assert.Equal("USB Audio DAC", name);
        Assert.Null(occurrenceIndex);
    }

    [Fact]
    public void ParsePersistedKey_RoundTripsWithComposePersistedKey()
    {
        var option = new AudioOutputDeviceService.Option(1, "USB Audio DAC", "USB Audio DAC (2)", OccurrenceIndex: 1);
        string key = AudioOutputDeviceService.ComposePersistedKey(option);

        (string name, int? occurrenceIndex) = AudioOutputDeviceService.ParsePersistedKey(key);

        Assert.Equal("USB Audio DAC", name);
        Assert.Equal(1, occurrenceIndex);
    }

    [Fact]
    public void ParsePersistedKey_MalformedSuffix_FallsBackToWholeStringAsName()
    {
        (string name, int? occurrenceIndex) = AudioOutputDeviceService.ParsePersistedKey("Weird\uE000Name");

        Assert.Equal("Weird\uE000Name", name);
        Assert.Null(occurrenceIndex);
    }
}
