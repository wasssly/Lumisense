using System;
using System.IO;
using Xunit;

namespace Lumisense.Tests;

public sealed class ReleaseListRetryPolicyTests
{
    [Fact]
    public void ShouldRetry_RetriesNetworkFailuresAndServerErrors()
    {
        Assert.True(ReleaseListRetryPolicy.ShouldRetry(null, null, null));
        Assert.True(ReleaseListRetryPolicy.ShouldRetry(500, null, null));
        Assert.True(ReleaseListRetryPolicy.ShouldRetry(503, null, null));
    }

    [Fact]
    public void ShouldRetry_DoesNotRetryClientErrorsOtherThanTheRateLimit()
    {
        Assert.False(ReleaseListRetryPolicy.ShouldRetry(404, null, null));
        Assert.False(ReleaseListRetryPolicy.ShouldRetry(401, null, null));
    }

    [Fact]
    public void ShouldRetry_RateLimitWithALongResetIsNotRetriedButAShortRetryAfterIs()
    {
        Assert.False(ReleaseListRetryPolicy.ShouldRetry(403, null, 0));
        Assert.False(ReleaseListRetryPolicy.ShouldRetry(403, TimeSpan.FromMinutes(20), 0));
        Assert.False(ReleaseListRetryPolicy.ShouldRetry(429, null, null));
        Assert.True(ReleaseListRetryPolicy.ShouldRetry(429, TimeSpan.FromSeconds(5), null));
        Assert.True(ReleaseListRetryPolicy.ShouldRetry(403, TimeSpan.FromSeconds(2), 0));
    }

    [Fact]
    public void DelayBeforeAttempt_GrowsAndStaysBounded()
    {
        Assert.Equal(TimeSpan.Zero, ReleaseListRetryPolicy.DelayBeforeAttempt(0));
        Assert.Equal(TimeSpan.FromSeconds(1), ReleaseListRetryPolicy.DelayBeforeAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(3), ReleaseListRetryPolicy.DelayBeforeAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(3), ReleaseListRetryPolicy.DelayBeforeAttempt(7));
    }

    [Fact]
    public void ParseRateLimitReset_ReadsUnixSeconds()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), ReleaseListRetryPolicy.ParseRateLimitReset("1790000000"));
        Assert.Null(ReleaseListRetryPolicy.ParseRateLimitReset("soon"));
        Assert.Null(ReleaseListRetryPolicy.ParseRateLimitReset(null));
    }
}

public sealed class ReleaseListCacheTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsTheListAndItsETag()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lumisense-releases-{Guid.NewGuid():N}.json");
        try
        {
            var data = new ReleaseListCacheData
            {
                ETag = "W/\"abc\"",
                FetchedAt = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
                Releases =
                {
                    new ReleaseListItem
                    {
                        Version = "1.23.0",
                        ExeDownloadUrl = "https://github.com/wasssly/Lumisense/releases/download/v1.23.0/Lumisense-1.23.0-Setup.exe",
                        ExeSha256 = "ab12",
                        IsPrerelease = false
                    }
                }
            };

            ReleaseListCache.TrySave(data, path);
            ReleaseListCacheData? loaded = ReleaseListCache.TryLoad(path);

            Assert.NotNull(loaded);
            Assert.Equal("W/\"abc\"", loaded!.ETag);
            Assert.Equal(data.FetchedAt, loaded.FetchedAt);
            Assert.Single(loaded.Releases);
            Assert.Equal("1.23.0", loaded.Releases[0].Version);
            Assert.Equal("ab12", loaded.Releases[0].ExeSha256);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Releases\":[]}")]
    public void Deserialize_IgnoresBrokenOrEmptyCaches(string json) =>
        Assert.Null(ReleaseListCache.Deserialize(json));

    [Fact]
    public void TryLoad_ReturnsNullWhenThereIsNoFile() =>
        Assert.Null(ReleaseListCache.TryLoad(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));
}
