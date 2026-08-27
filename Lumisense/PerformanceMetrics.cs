using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AudioPlayer;

// Локальная диагностика производительности: измеряет длительности операций, но намеренно не
// сохраняет путь, метаданные трека, текст песни или сетевые ответы. В журнал попадают только
// медленные загрузки/этапы, поэтому обычное прослушивание не засоряет log-файлы.
internal sealed class TrackLoadPerformanceMeasurement
{
    private const long SlowTrackLoadMilliseconds = 750;
    private const long SlowStageMilliseconds = 250;

    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<(string Name, long Milliseconds)> _stages = new();
    private readonly bool _traceAllLoads;
    private readonly Action<string> _log;
    private long _lastMarkMilliseconds;

    internal TrackLoadPerformanceMeasurement(bool traceAllLoads = false, Action<string>? log = null)
    {
        _traceAllLoads = traceAllLoads;
        _log = log ?? Logger.Info;
    }

    public void MarkStage(string name)
    {
        long elapsed = _total.ElapsedMilliseconds;
        _stages.Add((name, elapsed - _lastMarkMilliseconds));
        _lastMarkMilliseconds = elapsed;
    }

    public void Complete(bool succeeded)
    {
        long totalMilliseconds = _total.ElapsedMilliseconds;
        bool hasSlowStage = _stages.Any(stage => stage.Milliseconds >= SlowStageMilliseconds);
        if (!_traceAllLoads && totalMilliseconds < SlowTrackLoadMilliseconds && !hasSlowStage) return;

        var details = new StringBuilder();
        foreach ((string name, long milliseconds) in _stages)
        {
            if (details.Length > 0) details.Append(", ");
            details.Append(name).Append('=').Append(milliseconds).Append("ms");
        }

        string category = _traceAllLoads ? "TRACE track-load" : "PERF track-load";
        _log($"{category} {(succeeded ? "completed" : "failed")}: total={totalMilliseconds}ms; {details}");
    }
}
