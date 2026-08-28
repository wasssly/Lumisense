using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioPlaybackCoordinatorTests
{
    [Fact]
    public async Task EnterExclusiveAsync_SerializesOperations()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        using AudioPlaybackCoordinator.ExclusiveLease first =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken);

        Task<AudioPlaybackCoordinator.ExclusiveLease> secondTask =
            coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken);
        Task completed = await Task.WhenAny(
            secondTask,
            Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));

        Assert.NotSame(secondTask, completed);

        first.Dispose();
        using AudioPlaybackCoordinator.ExclusiveLease second =
            await secondTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BeginTrackLoadAsync_CancelsPreviousAndInvalidatesItsGeneration()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        using AudioPlaybackCoordinator.LoadOperation first =
            await coordinator.BeginTrackLoadAsync(TestContext.Current.CancellationToken);

        Task<AudioPlaybackCoordinator.LoadOperation> secondTask =
            coordinator.BeginTrackLoadAsync(TestContext.Current.CancellationToken);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(first.IsCurrent);

        first.Dispose();
        using AudioPlaybackCoordinator.LoadOperation second =
            await secondTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(second.Generation > first.Generation);
        Assert.True(second.IsCurrent);
        Assert.Equal(second.Generation, coordinator.CurrentGeneration);
    }

    [Fact]
    public async Task BeginTrackLoadAsync_CancellationRequestedBeforeGate_StopsWaitingOperation()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        using AudioPlaybackCoordinator.ExclusiveLease blocker =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<AudioPlaybackCoordinator.LoadOperation> loadTask = coordinator.BeginTrackLoadAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loadTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }
}
