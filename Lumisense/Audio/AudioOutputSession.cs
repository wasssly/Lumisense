using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lumisense;

/// <summary>
/// Owns one initialized audio output and its WASAPI endpoint.
/// The session deliberately does not select endpoints or decide playback policy.
/// </summary>
internal sealed class AudioOutputSession : IDisposable
{
    private IWavePlayer? _player;
    private MMDevice? _endpoint;
    private int _disposed;

    public IWavePlayer? Player => _player;
    public MMDevice? Endpoint => _endpoint;
    public bool IsAttached => _player is not null;

    public void Attach(IWavePlayer player, MMDevice endpoint)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(endpoint);

        DisposeAttachedOutput();
        _player = player;
        _endpoint = endpoint;
    }

    public void Initialize(ISampleProvider sampleProvider)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(sampleProvider);
        (_player ?? throw new InvalidOperationException("Аудиовывод ещё не подключён."))
            .Init(new SampleToWaveProvider(sampleProvider));
    }

    public void Play() => GetPlayer().Play();

    public void Pause() => GetPlayer().Pause();

    public void Stop() => GetPlayer().Stop();

    public void Release()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        DisposeAttachedOutput();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DisposeAttachedOutput();
    }

    private IWavePlayer GetPlayer()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _player ?? throw new InvalidOperationException("Аудиовывод ещё не подключён.");
    }

    private void DisposeAttachedOutput()
    {
        IWavePlayer? player = Interlocked.Exchange(ref _player, null);
        MMDevice? endpoint = Interlocked.Exchange(ref _endpoint, null);
        try
        {
            player?.Dispose();
        }
        finally
        {
            endpoint?.Dispose();
        }
    }
}
