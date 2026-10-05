using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class UpdateCheckerDownloadTests
{
    private const string TrustedUrl = "https://github.com/wasssly/Lumisense/releases/download/v1.2.3/Lumisense-1.2.3-Setup.exe";
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task Download_WithMatchingHash_ReturnsVerifiedFile()
    {
        using var client = new HttpClient(new StubHandler(() => Ok(Payload)));

        string path = await UpdateChecker.DownloadReleaseAssetAsync(
            TrustedUrl, "sha256:" + HashOf(Payload), ".exe", null, null, TestContext.Current.CancellationToken, client);
        try
        {
            Assert.EndsWith(".exe", path);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_WithWrongHash_ThrowsAndLeavesNoFile()
    {
        var handler = new StubHandler(() => Ok(Payload));
        using var client = new HttpClient(handler);
        string wrongHash = HashOf([9, 9, 9]);

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadReleaseAssetAsync(
            TrustedUrl, wrongHash, ".exe", null, null, TestContext.Current.CancellationToken, client));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Download_RejectsOversizedContentLengthBeforeReadingBody()
    {
        using var client = new HttpClient(new StubHandler(() =>
        {
            HttpResponseMessage response = Ok(Payload);
            response.Content.Headers.ContentLength = 300L * 1024 * 1024;
            return response;
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadReleaseAssetAsync(
            TrustedUrl, HashOf(Payload), ".exe", null, null, TestContext.Current.CancellationToken, client));
    }

    [Fact]
    public async Task Download_DoesNotRetryClientErrors()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => UpdateChecker.DownloadReleaseAssetAsync(
            TrustedUrl, HashOf(Payload), ".exe", null, null, TestContext.Current.CancellationToken, client));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Download_RejectsUntrustedUrlWithoutNetworkAccess()
    {
        var handler = new StubHandler(() => Ok(Payload));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateChecker.DownloadReleaseAssetAsync(
            "https://evil.example/Lumisense-1.2.3-Setup.exe", HashOf(Payload), ".exe", null, null,
            TestContext.Current.CancellationToken, client));

        Assert.Equal(0, handler.Calls);
    }
}
