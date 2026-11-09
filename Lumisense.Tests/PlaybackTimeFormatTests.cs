using System;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class PlaybackTimeFormatTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(65, "01:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(4500, "1:15:00")]
    public void Format_KeepsHoursForLongTracks(int seconds, string expected)
    {
        Assert.Equal(expected, PlaybackTimeFormat.Format(TimeSpan.FromSeconds(seconds)));
    }
}
