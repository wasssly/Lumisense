using System.Diagnostics;
using System.Text;

namespace Lumisense;

// Замер этапов холодного старта от начала процесса: по одной строке в логе видно, где уходит время до показа окна.
internal static class StartupTrace
{
    private static readonly object Gate = new();
    private static readonly List<(string Name, long Milliseconds)> Marks = new();
    private static readonly List<(string Name, long Milliseconds)> Inner = new();
    private static long _last;

    public static void Mark(string name)
    {
        long sinceProcessStart = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        lock (Gate)
        {
            Marks.Add((name, sinceProcessStart - _last));
            _last = sinceProcessStart;
        }
    }

    // Вложенный замер внутри одного этапа: попадает в скобки отдельно и не вычитается из соседних этапов.
    public static T Time<T>(string name, Func<T> action)
    {
        var timer = Stopwatch.StartNew();
        T result = action();
        lock (Gate) Inner.Add((name, timer.ElapsedMilliseconds));
        return result;
    }

    public static void Flush()
    {
        string line;
        lock (Gate)
        {
            var text = new StringBuilder();
            foreach ((string name, long milliseconds) in Marks)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(name).Append('=').Append(milliseconds).Append("ms");
            }

            foreach ((string name, long milliseconds) in Inner)
                text.Append(text.Length > 0 ? ", " : string.Empty).Append('(').Append(name).Append('=').Append(milliseconds).Append("ms)");

            line = $"TRACE startup (с начала процесса): {text}";
            Marks.Clear();
            Inner.Clear();
        }

        Logger.Info(line);
    }
}
