using System;

namespace Lumisense;

/// <summary>
/// Определяет, нужно ли пропустить следующий slide-переход обложки во время короткой серии
/// навигационных запросов. Не зависит от WPF и поэтому покрывается unit-тестами.
/// </summary>
public sealed class AlbumArtTransitionBurstPolicy
{
    private readonly TimeSpan _burstWindow;
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public AlbumArtTransitionBurstPolicy(TimeSpan burstWindow)
    {
        if (burstWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(burstWindow));

        _burstWindow = burstWindow;
    }

    /// <summary>
    /// Возвращает true, если запрос находится внутри burst-окна после предыдущего запроса.
    /// Текущий запрос всегда становится новой точкой отсчёта, поэтому удерживаемая клавиша
    /// не запускает анимацию через один шаг.
    /// </summary>
    public bool ShouldSkipAnimation(DateTime requestUtc)
    {
        bool isBurst = _lastRequestUtc != DateTime.MinValue && requestUtc >= _lastRequestUtc &&
            requestUtc - _lastRequestUtc < _burstWindow;
        _lastRequestUtc = requestUtc;
        return isBurst;
    }

    public void Reset() => _lastRequestUtc = DateTime.MinValue;
}
