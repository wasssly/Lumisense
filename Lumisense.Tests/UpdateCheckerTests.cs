using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class UpdateCheckerTests
{
    private const string Hex64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Hex64)]
    [InlineData("sha256:" + Hex64)]
    [InlineData("SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void TryParseSha256_AcceptsPlainAndPrefixedDigests(string value)
    {
        Assert.True(UpdateChecker.TryParseSha256(value, out byte[] hash));
        Assert.Equal(32, hash.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:")]
    [InlineData("sha256:0123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void TryParseSha256_RejectsMissingOrMalformedDigests(string? value)
    {
        Assert.False(UpdateChecker.TryParseSha256(value, out byte[] hash));
        Assert.Empty(hash);
    }

    [Theory]
    [InlineData("https://github.com/wasssly/Lumisense/releases/download/v1.2.3/Lumisense-1.2.3-Setup.exe")]
    [InlineData("https://gh-proxy.org/https://github.com/wasssly/Lumisense/releases/download/v1.2.3/a.exe")]
    [InlineData("https://v4.gh-proxy.org/https://github.com/wasssly/Lumisense/releases/download/v1.2.3/a.exe")]
    [InlineData("https://gh-proxy.com/https://github.com/wasssly/Lumisense/releases/download/v1.2.3/a.exe")]
    [InlineData("https://ghfast.top/https://github.com/wasssly/Lumisense/releases/download/v1.2.3/a.exe")]
    public void TryValidateDownloadUrl_AcceptsTrustedHosts(string url)
    {
        Assert.True(UpdateChecker.TryValidateDownloadUrl(url, out _));
    }

    [Theory]
    [InlineData("http://github.com/wasssly/Lumisense/releases/download/v1.2.3/a.exe")]
    [InlineData("https://github.com/someone-else/Lumisense/releases/download/v1.2.3/a.exe")]
    [InlineData("https://github.com/wasssly/Lumisense/archive/main.zip")]
    [InlineData("https://gh-proxy.org.evil.example/a.exe")]
    [InlineData("https://evilgh-proxy.org/a.exe")]
    [InlineData("https://objects.githubusercontent.com/a.exe")]
    [InlineData("/wasssly/Lumisense/releases/download/v1.2.3/a.exe")]
    [InlineData("")]
    public void TryValidateDownloadUrl_RejectsUntrustedAddresses(string url)
    {
        Assert.False(UpdateChecker.TryValidateDownloadUrl(url, out _));
    }

    [Theory]
    [InlineData("https://github.com/wasssly/Lumisense/releases/tag/v1.2.3", true)]
    [InlineData("http://github.com/wasssly/Lumisense/releases/tag/v1.2.3", false)]
    [InlineData("https://github.com/other/repo/releases/tag/v1.2.3", false)]
    [InlineData("https://github.com.evil.example/wasssly/Lumisense/", false)]
    public void IsTrustedReleaseNotesUrl_OnlyAcceptsOwnRepositoryOverHttps(string url, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsTrustedReleaseNotesUrl(url));
    }

    [Theory]
    [InlineData("1.22.1", "1.22.0", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.22.0", "1.22.0", false)]
    [InlineData("1.21.9", "1.22.0", false)]
    public void IsNewer_ComparesVersionsNumerically(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(latest, current));
    }
}
