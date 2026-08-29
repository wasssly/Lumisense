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

    [Fact]
    public async Task BeginTrackLoadAsync_ManySequentialRequests_LeavesOnlyLastRequestActive()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        using AudioPlaybackCoordinator.LoadOperation first =
            await coordinator.BeginTrackLoadAsync(TestContext.Current.CancellationToken);

        const int requestCount = 100;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var pendingLoads = new List<Task<AudioPlaybackCoordinator.LoadOperation>>(requestCount);
        for (int i = 0; i < requestCount; i++)
        {
            pendingLoads.Add(coordinator.BeginTrackLoadAsync(cancellationToken));
        }

        Assert.All(pendingLoads, task => Assert.False(task.IsCompletedSuccessfully));
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.Equal(requestCount + 1, coordinator.CurrentGeneration);

        first.Dispose();

        for (int i = 0; i < pendingLoads.Count - 1; i++)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pendingLoads[i].WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }

        using AudioPlaybackCoordinator.LoadOperation last =
            await pendingLoads[^1].WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(last.IsCurrent);
        Assert.Equal(requestCount + 1, last.Generation);
    }

    [Fact]
    public async Task BeginTrackLoadAsync_ParallelRequests_AllowOnlyFinalGenerationToAcquireGate()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        using AudioPlaybackCoordinator.ExclusiveLease blocker =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken);

        const int requestCount = 64;
        int started = 0;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var allStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<Task<AudioPlaybackCoordinator.LoadOperation>>(requestCount);

        for (int i = 0; i < requestCount; i++)
        {
            requests.Add(Task.Run(async () =>
            {
                if (Interlocked.Increment(ref started) == requestCount)
                {
                    allStarted.SetResult(true);
                }

                await release.Task.WaitAsync(cancellationToken);
                return await coordinator.BeginTrackLoadAsync(cancellationToken);
            }, cancellationToken));
        }

        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        release.SetResult(true);
        await WaitUntilAsync(
            () => coordinator.CurrentGeneration == requestCount,
            TestContext.Current.CancellationToken);

        blocker.Dispose();

        int successfulRequests = 0;
        foreach (Task<AudioPlaybackCoordinator.LoadOperation> request in requests)
        {
            try
            {
                using AudioPlaybackCoordinator.LoadOperation operation =
                    await request.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                successfulRequests++;
                Assert.Equal(requestCount, operation.Generation);
                Assert.True(operation.IsCurrent);
            }
            catch (OperationCanceledException)
            {
                // Every superseded request is expected to be cancelled.
            }
        }

        Assert.Equal(1, successfulRequests);
        Assert.Equal(requestCount, coordinator.CurrentGeneration);
    }

    [Fact]
    public async Task Dispose_DuringActiveTrackLoad_CancelsOperationAndRejectsNewLoads()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        AudioPlaybackCoordinator.LoadOperation operation =
            await coordinator.BeginTrackLoadAsync(TestContext.Current.CancellationToken);

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        operation.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.BeginTrackLoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_CanBeCalledRepeatedlyWithoutThrowing()
    {
        var coordinator = new AudioPlaybackCoordinator();

        coordinator.Dispose();
        coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lease_DisposeIsIdempotent_AndDoesNotCorruptGate()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        AudioPlaybackCoordinator.ExclusiveLease lease =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken);

        lease.Dispose();
        lease.Dispose();

        using AudioPlaybackCoordinator.ExclusiveLease nextLease =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledLoadBeforeGate_ReleasesGateForNextExclusiveOperation()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        using AudioPlaybackCoordinator.ExclusiveLease blocker =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<AudioPlaybackCoordinator.LoadOperation> cancelledLoad =
            coordinator.BeginTrackLoadAsync(cts.Token);
        cts.Cancel();
        blocker.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledLoad.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        using AudioPlaybackCoordinator.ExclusiveLease nextLease =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledLoad_DoesNotPreventSubsequentTrackLoad()
    {
        using var coordinator = new AudioPlaybackCoordinator();
        using AudioPlaybackCoordinator.ExclusiveLease blocker =
            await coordinator.EnterExclusiveAsync(TestContext.Current.CancellationToken);
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<AudioPlaybackCoordinator.LoadOperation> cancelledLoad =
            coordinator.BeginTrackLoadAsync(firstCts.Token);
        firstCts.Cancel();
        blocker.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledLoad.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        using AudioPlaybackCoordinator.LoadOperation nextLoad =
            await coordinator.BeginTrackLoadAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(nextLoad.IsCurrent);
        Assert.Equal(coordinator.CurrentGeneration, nextLoad.Generation);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        await WaitUntilCoreAsync(condition, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
    }

    private static async Task WaitUntilCoreAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }
}
