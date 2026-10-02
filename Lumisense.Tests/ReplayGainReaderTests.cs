using System.Collections.Generic;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class ReplayGainReaderTests
{
    private static ATL.Track TrackWith(params (string Key, string Value)[] fields)
    {
        var track = new ATL.Track { AdditionalFields = new Dictionary<string, string>() };
        foreach (var (key, value) in fields)
            track.AdditionalFields[key] = value;
        return track;
    }

    private static double Gain(params (string Key, string Value)[] fields) =>
        ReplayGainReader.GetTrackGainLinear(TrackWith(fields));

    [Fact]
    public void TrackWithoutReplayGainTags_UsesUnityGain()
    {
        Assert.Equal(1.0, Gain());
    }

    [Fact]
    public void TrackWithoutAdditionalFields_UsesUnityGain()
    {
        Assert.Equal(1.0, ReplayGainReader.GetTrackGainLinear(new ATL.Track()));
    }

    [Fact]
    public void MissingFile_UsesUnityGain()
    {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumisense-missing-" + System.Guid.NewGuid().ToString("N") + ".mp3");

        Assert.Equal(1.0, ReplayGainReader.GetTrackGainLinear(missing));
    }

    [Theory]
    [InlineData("-6.00 dB", 0.501187)]
    [InlineData("-6", 0.501187)]
    [InlineData("-6.00 DB", 0.501187)]
    [InlineData("  -6.00dB  ", 0.501187)]
    [InlineData("+3.20 dB", 1.445440)]
    [InlineData("0.00 dB", 1.0)]
    public void GainTag_IsConvertedFromDecibelsToLinear(string raw, double expected)
    {
        Assert.Equal(expected, Gain(("replaygain_track_gain", raw)), 5);
    }

    [Fact]
    public void UpperCaseTagNamesAreRecognized()
    {
        Assert.Equal(0.501187, Gain(("REPLAYGAIN_TRACK_GAIN", "-6.00 dB")), 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loud")]
    [InlineData("dB")]
    public void UnreadableGainValue_UsesUnityGain(string raw)
    {
        Assert.Equal(1.0, Gain(("replaygain_track_gain", raw)));
    }

    [Fact]
    public void PeakWithoutGain_DoesNotChangeVolume()
    {
        Assert.Equal(1.0, Gain(("replaygain_track_peak", "0.5")));
    }

    [Fact]
    public void Peak_LimitsAGainThatWouldClip()
    {
        // +6 dB даёт ~1.995, но пик 0.8 допускает усиление не больше 1 / 0.8 = 1.25.
        Assert.Equal(1.25, Gain(("replaygain_track_gain", "+6.00 dB"), ("replaygain_track_peak", "0.8")), 5);
    }

    [Fact]
    public void Peak_DoesNotRaiseAGainThatAttenuates()
    {
        Assert.Equal(0.501187, Gain(("replaygain_track_gain", "-6.00 dB"), ("replaygain_track_peak", "0.8")), 5);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("peak")]
    public void NonPositiveOrUnreadablePeak_IsIgnored(string peak)
    {
        Assert.Equal(1.995262, Gain(("replaygain_track_gain", "+6.00 dB"), ("replaygain_track_peak", peak)), 5);
    }
}
