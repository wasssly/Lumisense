using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class AudioOutputSessionTests
{
    [Fact]
    public void NewSession_HasNoAttachedOutput()
    {
        using var session = new AudioOutputSession();

        Assert.False(session.IsAttached);
        Assert.Null(session.Player);
        Assert.Null(session.Endpoint);
    }

    [Fact]
    public void ReleaseWithoutAttachedOutput_IsSafeAndIdempotent()
    {
        using var session = new AudioOutputSession();

        session.Release();
        session.Release();

        Assert.False(session.IsAttached);
    }

    [Fact]
    public void DisposedSession_RejectsOutputOperations()
    {
        var session = new AudioOutputSession();
        session.Dispose();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Release());
        Assert.Throws<ObjectDisposedException>(() => session.Play());
        Assert.Throws<ObjectDisposedException>(() => session.Pause());
        Assert.Throws<ObjectDisposedException>(() => session.Stop());
    }
}
