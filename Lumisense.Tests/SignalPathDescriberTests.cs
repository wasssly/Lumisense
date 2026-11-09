using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class SignalPathDescriberTests
{
    private static SignalPathState State(
        bool exclusive = true,
        string? outputFormat = "44.1 kHz · 2 ch · 32-bit",
        bool equalizer = false,
        bool volumeScaled = false) =>
        new(44100, 2, ".flac", exclusive, outputFormat, false, false, equalizer, false, false, volumeScaled, true);

    [Fact]
    public void ExclusiveWithoutProcessingAtFileRate_IsBitPerfect()
    {
        var description = SignalPathDescriber.Describe(State(), localized: false);

        Assert.True(description.IsBitPerfect);
        Assert.Contains("FLAC 44.1 kHz", description.Text);
        Assert.Contains("WASAPI Exclusive", description.Text);
    }

    [Fact]
    public void ProcessingSharedModeOrRateMismatch_IsNotBitPerfect()
    {
        Assert.False(SignalPathDescriber.Describe(State(equalizer: true), false).IsBitPerfect);
        Assert.False(SignalPathDescriber.Describe(State(volumeScaled: true), false).IsBitPerfect);
        Assert.False(SignalPathDescriber.Describe(State(exclusive: false), false).IsBitPerfect);
        Assert.False(SignalPathDescriber.Describe(State(outputFormat: "48 kHz · 2 ch · 32-bit"), false).IsBitPerfect);
    }

    [Fact]
    public void ActiveStages_AreListedInOrder()
    {
        var text = SignalPathDescriber.Describe(State(equalizer: true, volumeScaled: true), false).Text;

        Assert.True(text.IndexOf("Equalizer") < text.IndexOf("Volume"));
        Assert.True(text.IndexOf("Volume") < text.IndexOf("WASAPI"));
    }
}
