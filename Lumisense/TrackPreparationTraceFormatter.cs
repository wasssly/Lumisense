namespace Lumisense;

// Opt-in диагностика холодной подготовки трека. Принимает только длительности — ни путей,
// ни тегов, ни обложек, ни сетевых данных.
internal static class TrackPreparationTraceFormatter
{
    internal static string Format(
        long replayGainMilliseconds,
        long tagsMilliseconds,
        long embeddedArtworkMilliseconds,
        long audioFileReaderMilliseconds,
        long pipelineMilliseconds = 0) =>
        $"TRACE track-prepare: replay-gain={Math.Max(0, replayGainMilliseconds)}ms; " +
        $"tags={Math.Max(0, tagsMilliseconds)}ms; " +
        $"embedded-artwork={Math.Max(0, embeddedArtworkMilliseconds)}ms; " +
        $"audio-file-reader={Math.Max(0, audioFileReaderMilliseconds)}ms; " +
        $"pipeline={Math.Max(0, pipelineMilliseconds)}ms";
}
