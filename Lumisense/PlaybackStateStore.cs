using System;

namespace Lumisense;

// Независимый от WPF/NAudio снимок состояния, достаточный для мини-плеера, Now Playing,
// интеграций и будущих подписчиков. Обложка и кисти намеренно не входят сюда: это ресурсы
// UI-потока, которые по-прежнему доставляются узким визуальным событием TrackInfoChanged.
public sealed record PlaybackSnapshot(
    string? TrackPath,
    string Title,
    string Artist,
    bool IsPlaying,
    double PositionSeconds,
    double DurationSeconds)
{
    public static PlaybackSnapshot Empty { get; } = new(null, string.Empty, string.Empty, false, 0, 0);

    public double ProgressRatio => DurationSeconds > 0
        ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1)
        : 0;
}

// Единая точка публикации runtime-состояния. Хранилище потокобезопасно для чтения, однако
// события намеренно доставляются в том потоке, где MainWindow опубликовал снимок (UI-поток).
// Это защищает WPF-подписчиков от фоновых обращений к элементам интерфейса.
public sealed class PlaybackStateStore
{
    private readonly object _gate = new();
    private PlaybackSnapshot _current = PlaybackSnapshot.Empty;

    public PlaybackSnapshot Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event Action<PlaybackSnapshot>? Changed;

    public void Publish(PlaybackSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        Action<PlaybackSnapshot>? handlers;
        lock (_gate)
        {
            if (_current == snapshot) return;
            _current = snapshot;
            handlers = Changed;
        }

        handlers?.Invoke(snapshot);
    }
}
