using System.Threading;

namespace Lumisense;

/// <summary>
/// Serializes operations that replace the active audio graph and cancels superseded track loads.
/// </summary>
internal sealed class AudioPlaybackCoordinator : IDisposable
{
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private CancellationTokenSource? _activeLoadCts;
    private int _generation;
    private int _disposed;

    public int CurrentGeneration => Volatile.Read(ref _generation);

    public async Task<ExclusiveLease> EnterExclusiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _audioGate.WaitAsync(cancellationToken);
        return new ExclusiveLease(this);
    }

    public async Task<LoadOperation> BeginTrackLoadAsync(CancellationToken lifetimeToken)
    {
        ThrowIfDisposed();

        var loadCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _activeLoadCts, loadCts);
        // Освобождается владельцем предыдущего LoadOperation после завершения его finally.
        // До этого момента его CancellationToken может использоваться отменённой задачей.
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Предыдущая операция уже завершилась и освободила свой CTS.
        }

        int generation = Interlocked.Increment(ref _generation);
        bool gateAcquired = false;
        try
        {
            await _audioGate.WaitAsync(loadCts.Token);
            gateAcquired = true;
            loadCts.Token.ThrowIfCancellationRequested();

            LoadOperation operation = new(this, loadCts, generation);
            gateAcquired = false;
            return operation;
        }
        catch
        {
            if (gateAcquired)
            {
                _audioGate.Release();
            }

            Interlocked.CompareExchange(ref _activeLoadCts, null, loadCts);
            loadCts.Dispose();
            throw;
        }
    }

    public bool IsCurrentGeneration(int generation) =>
        Volatile.Read(ref _generation) == generation && Volatile.Read(ref _disposed) == 0;

    public void CancelCurrentLoad()
    {
        try
        {
            Volatile.Read(ref _activeLoadCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The load completed concurrently with the cancellation request.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // CTS освобождается самим LoadOperation после выхода из его finally-блока.
        // Здесь только запрашиваем отмену, чтобы не обнулить токен у ещё выполняющейся задачи.
        try
        {
            Volatile.Read(ref _activeLoadCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Активная операция завершилась одновременно с закрытием coordinator.
        }
    }

    private void Complete(ExclusiveLease lease)
    {
        if (Interlocked.Exchange(ref lease.Disposed, 1) != 0)
            return;

        if (lease is LoadOperation loadOperation)
        {
            Interlocked.CompareExchange(ref _activeLoadCts, null, loadOperation.CancellationSource);
            loadOperation.CancellationSource.Dispose();
        }

        _audioGate.Release();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AudioPlaybackCoordinator));
    }

    internal class ExclusiveLease : IDisposable
    {
        protected readonly AudioPlaybackCoordinator Owner;
        internal int Disposed;

        internal ExclusiveLease(AudioPlaybackCoordinator owner)
        {
            Owner = owner;
        }

        public void Dispose() => Owner.Complete(this);
    }

    internal sealed class LoadOperation : ExclusiveLease
    {
        internal readonly CancellationTokenSource CancellationSource;

        internal LoadOperation(AudioPlaybackCoordinator owner, CancellationTokenSource cancellationSource, int generation)
            : base(owner)
        {
            CancellationSource = cancellationSource;
            Generation = generation;
        }

        public int Generation { get; }
        public CancellationToken CancellationToken => CancellationSource.Token;

        public bool IsCurrent => Owner.IsCurrentGeneration(Generation);

    }
}
