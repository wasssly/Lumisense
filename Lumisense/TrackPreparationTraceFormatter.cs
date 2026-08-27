namespace AudioPlayer;

// Форматирует opt-in диагностику холодной подготовки трека. Преднамеренно принимает только
// длительности: в trace не попадают путь, название, исполнитель, теги, содержимое обложки
// или сетевые данные.
internal static class TrackPreparationTraceFormatter
{
    internal static string Format(
        long replayGainMilliseconds,
        long tagsMilliseconds,
        long embeddedArtworkMilliseconds,
        long audioFileReaderMilliseconds) =>
        $"TRACE track-prepare: replay-gain={Math.Max(0, replayGainMilliseconds)}ms; " +
        $"tags={Math.Max(0, tagsMilliseconds)}ms; " +
        $"embedded-artwork={Math.Max(0, embeddedArtworkMilliseconds)}ms; " +
        $"audio-file-reader={Math.Max(0, audioFileReaderMilliseconds)}ms";
}
