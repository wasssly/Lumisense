using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumisense;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class WaveformGeneratorTests : IDisposable
{
    private const int SampleRate = 44100;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-waveform-tests-" + Guid.NewGuid().ToString("N"));

    public WaveformGeneratorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    // Моно 16 бит: каждая секция задаётся амплитудой (0..1) и длиной в секундах.
    private string WriteWav(params (double Amplitude, double Seconds)[] sections)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        foreach (var (amplitude, seconds) in sections)
        {
            int samples = (int)(SampleRate * seconds);
            short value = (short)(amplitude * short.MaxValue);
            for (int i = 0; i < samples; i++)
                writer.WriteSample(i % 2 == 0 ? value / (float)short.MaxValue : -value / (float)short.MaxValue);
        }

        return path;
    }

    [Fact]
    public async Task GenerateAsync_ReturnsFixedBucketCountNormalizedToOne()
    {
        string path = WriteWav((0.8, 1.0), (0.2, 1.0));

        float[]? peaks = await WaveformGenerator.GenerateAsync(path);

        Assert.NotNull(peaks);
        Assert.Equal(WaveformGenerator.BucketCount, peaks!.Length);
        Assert.InRange(peaks.Max(), 0.999f, 1f);
        Assert.All(peaks, peak => Assert.InRange(peak, 0f, 1f));
    }

    [Fact]
    public async Task GenerateAsync_LoudStartAndQuietEnd_KeepsShapeOverTime()
    {
        string path = WriteWav((0.8, 1.0), (0.2, 1.0));

        float[]? peaks = await WaveformGenerator.GenerateAsync(path);

        Assert.NotNull(peaks);
        Assert.True(peaks![10] > 0.9f, "начало громкое");
        Assert.True(peaks[WaveformGenerator.BucketCount - 10] < 0.5f, "конец тихий");
    }

    [Fact]
    public async Task GenerateAsync_SilentFile_ReturnsZeroPeaks()
    {
        string path = WriteWav((0.0, 1.0));

        float[]? peaks = await WaveformGenerator.GenerateAsync(path);

        Assert.NotNull(peaks);
        Assert.Equal(WaveformGenerator.BucketCount, peaks!.Length);
        Assert.All(peaks, peak => Assert.Equal(0f, peak));
    }

    [Fact]
    public async Task GenerateAsync_VeryShortFile_StillReturnsAllBuckets()
    {
        string path = WriteWav((0.5, 0.01));

        float[]? peaks = await WaveformGenerator.GenerateAsync(path);

        Assert.NotNull(peaks);
        Assert.Equal(WaveformGenerator.BucketCount, peaks!.Length);
    }

    [Fact]
    public async Task GenerateAsync_MissingFile_ReturnsNull()
    {
        float[]? peaks = await WaveformGenerator.GenerateAsync(Path.Combine(_directory, "missing.wav"));

        Assert.Null(peaks);
    }

    [Fact]
    public async Task GenerateAsync_CorruptFile_ReturnsNull()
    {
        string path = Path.Combine(_directory, "corrupt.wav");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 });

        float[]? peaks = await WaveformGenerator.GenerateAsync(path);

        Assert.Null(peaks);
    }

    [Fact]
    public async Task GenerateAsync_AlreadyCancelled_ThrowsOperationCanceled()
    {
        string path = WriteWav((0.5, 0.5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaveformGenerator.GenerateAsync(path, cts.Token));
    }
}
