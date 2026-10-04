using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// Только то, что не требует декодирования аудио: проверка аргументов, отмена и очистка после ошибки.
public sealed class TrackExportServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-export-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TrackExportService _service = new();
    private readonly TrackExportOptions _options = new(PlaybackSpeed: 1.0, PlaybackPitchSemitones: 0.0);

    public TrackExportServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportMp3Async_RejectsBlankSourcePath(string? sourcePath)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _service.ExportMp3Async(sourcePath!, Path.Combine(_directory, "out.mp3"), _options, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportMp3Async_RejectsBlankDestinationPath(string? destinationPath)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _service.ExportMp3Async(Path.Combine(_directory, "in.mp3"), destinationPath!, _options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportMp3Async_RejectsMissingOptions()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.ExportMp3Async(Path.Combine(_directory, "in.mp3"), Path.Combine(_directory, "out.mp3"), null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportMp3Async_RejectsDestinationWithoutDirectory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ExportMp3Async("in.mp3", "out.mp3", _options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportMp3Async_AlreadyCancelledToken_ThrowsOperationCanceledAndWritesNothing()
    {
        string destination = Path.Combine(_directory, "out.mp3");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.ExportMp3Async(Path.Combine(_directory, "in.mp3"), destination, _options, cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".partial"));
    }

    [Fact]
    public async Task ExportMp3Async_MissingSource_FailsAndLeavesNoFilesBehind()
    {
        string destination = Path.Combine(_directory, "exports", "out.mp3");

        Exception? failure = await Record.ExceptionAsync(() =>
            _service.ExportMp3Async(Path.Combine(_directory, "missing.mp3"), destination, _options, TestContext.Current.CancellationToken));

        Assert.NotNull(failure);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".partial"));
    }
}
