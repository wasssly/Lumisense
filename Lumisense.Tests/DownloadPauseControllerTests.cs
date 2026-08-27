using System;
using System.Threading;
using System.Threading.Tasks;
using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class DownloadPauseControllerTests
{
    // Верхняя граница ожидания для тестов, которые не должны блокироваться вечно — если
    // регрессия заставит WaitIfPausedAsync зависнуть, тест упадёт по таймауту, а не подвесит CI.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public void IsPaused_InitiallyFalse()
    {
        using var controller = new DownloadPauseController();

        Assert.False(controller.IsPaused);
    }

    [Fact]
    public void Pause_SetsIsPausedTrue()
    {
        using var controller = new DownloadPauseController();

        controller.Pause();

        Assert.True(controller.IsPaused);
    }

    [Fact]
    public void Resume_AfterPause_SetsIsPausedFalse()
    {
        using var controller = new DownloadPauseController();
        controller.Pause();

        controller.Resume();

        Assert.False(controller.IsPaused);
    }

    [Fact]
    public void Pause_CalledTwice_IsIdempotent()
    {
        using var controller = new DownloadPauseController();

        controller.Pause();
        controller.Pause();

        Assert.True(controller.IsPaused);
    }

    [Fact]
    public void Resume_WithoutPause_IsNoOp()
    {
        using var controller = new DownloadPauseController();

        controller.Resume();

        Assert.False(controller.IsPaused);
    }

    [Fact]
    public async Task WaitIfPausedAsync_WhenNotPaused_CompletesImmediately()
    {
        using var controller = new DownloadPauseController();

        Task waitTask = controller.WaitIfPausedAsync(CancellationToken.None);
        Task completed = await Task.WhenAny(waitTask, Task.Delay(HangGuard, TestContext.Current.CancellationToken));

        Assert.Same(waitTask, completed);
    }

    [Fact]
    public async Task WaitIfPausedAsync_WhenPaused_CompletesOnlyAfterResume()
    {
        using var controller = new DownloadPauseController();
        controller.Pause();

        Task waitTask = controller.WaitIfPausedAsync(CancellationToken.None);

        // Пока не вызван Resume, ожидание не должно завершаться — победить должен таймер.
        Task stillWaiting = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.NotSame(waitTask, stillWaiting);

        controller.Resume();
        Task completedAfterResume = await Task.WhenAny(waitTask, Task.Delay(HangGuard, TestContext.Current.CancellationToken));
        Assert.Same(waitTask, completedAfterResume);
    }

    [Fact]
    public async Task WaitIfPausedAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        using var controller = new DownloadPauseController();
        controller.Pause();
        using var cts = new CancellationTokenSource();

        Task waitTask = controller.WaitIfPausedAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask.WaitAsync(HangGuard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_WhilePaused_ReleasesPendingWait()
    {
        var controller = new DownloadPauseController();
        controller.Pause();
        Task waitTask = controller.WaitIfPausedAsync(CancellationToken.None);

        controller.Dispose();

        Task completed = await Task.WhenAny(waitTask, Task.Delay(HangGuard, TestContext.Current.CancellationToken));
        Assert.Same(waitTask, completed);
    }

    [Fact]
    public void Pause_AfterDispose_IsNoOp()
    {
        var controller = new DownloadPauseController();
        controller.Dispose();

        controller.Pause();

        Assert.False(controller.IsPaused);
    }
}
