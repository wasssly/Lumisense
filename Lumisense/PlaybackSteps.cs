namespace Lumisense;

// Шаги пользовательских жестов управления воспроизведением: колесо мыши, глобальные хоткеи и остров используют одни и те же числа.
internal static class PlaybackSteps
{
    /// <summary>Громкость на одно деление колеса и одно нажатие хоткея (доля от 0 до 1).</summary>
    public const double Volume = 0.02;

    /// <summary>Громкость на один жест на обложке: крупнее, потому что жест реже и «тяжелее» колеса.</summary>
    public const double CoverGestureVolume = 0.04;

    /// <summary>Перемотка в секундах на одно деление колеса над полосой и одно нажатие хоткея.</summary>
    public const double SeekSeconds = 5;
}
