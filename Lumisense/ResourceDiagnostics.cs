using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace Lumisense;

// Встроенная проверка ресурсов самого плеера: нагрузка процессора, память, дескрипторы, GDI- и USER-объекты, сборки мусора.
// Ничего не отправляет по сети и не меняет поведение плеера; данные живут в памяти, а отчёт сохраняется только по кнопке.

internal sealed record ResourceSample(
    double Minute,
    double CpuPercentOfOneCore,
    double PrivateMiB,
    double WorkingSetMiB,
    double ManagedMiB,
    int Handles,
    int Threads,
    int GdiObjects,
    int UserObjects,
    int Gen2Collections,
    int TrackChanges,
    double IoWriteMiB = 0);

internal sealed record ResourceSummary(
    int SampleCount,
    double DurationMinutes,
    bool TrendReliable,
    double CpuAverage,
    double CpuP95,
    double CpuMax,
    double PrivateStartMiB,
    double PrivateEndMiB,
    double PrivatePeakMiB,
    double ManagedEndMiB,
    double PrivateSlopePerHour,
    double HandlesSlopePerHour,
    double GdiSlopePerHour,
    double UserSlopePerHour,
    double PrivateGrowthMiB,
    double HandlesGrowth,
    double GdiGrowth,
    double UserGrowth,
    int Handles,
    int Threads,
    int GdiObjects,
    int UserObjects,
    int Gen2Delta,
    int TrackChanges,
    double DiskWrittenKiB = 0);

internal enum ResourceVerdict { NotEnoughData, Normal, SuspectedLeak }

internal sealed record ResourceAssessment(ResourceVerdict Verdict, IReadOnlyList<string> Reasons);

internal sealed class ResourceSampler : IDisposable
{
    private const double MiB = 1024.0 * 1024.0;

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastCpu;
    private double _lastSeconds;

    public ResourceSampler() => _lastCpu = _process.TotalProcessorTime;

    public ResourceSample Read(int trackChanges)
    {
        _process.Refresh();
        double seconds = _clock.Elapsed.TotalSeconds;
        TimeSpan cpu = _process.TotalProcessorTime;
        double cpuPercent = seconds > _lastSeconds ? (cpu - _lastCpu).TotalSeconds / (seconds - _lastSeconds) * 100.0 : 0.0;
        _lastCpu = cpu;
        _lastSeconds = seconds;

        IntPtr handle = _process.Handle;
        return new ResourceSample(
            seconds / 60.0,
            Math.Max(0.0, cpuPercent),
            _process.PrivateMemorySize64 / MiB,
            _process.WorkingSet64 / MiB,
            GC.GetTotalMemory(forceFullCollection: false) / MiB,
            _process.HandleCount,
            _process.Threads.Count,
            ReadGuiObjects(handle, 0),
            ReadGuiObjects(handle, 1),
            GC.CollectionCount(2),
            trackChanges,
            ReadIoWriteMiB(handle));
    }

    public void Dispose() => _process.Dispose();

    // Счётчики GDI и USER есть только в Windows; на другой системе (и в тестах вне Windows) они просто равны нулю.
    private static int ReadGuiObjects(IntPtr process, uint flags)
    {
        try
        {
            return (int)GetGuiResources(process, flags);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static double ReadIoWriteMiB(IntPtr process)
    {
        try
        {
            return GetProcessIoCounters(process, out IoCounters counters) ? counters.WriteTransferCount / MiB : 0.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0.0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}

// Время последнего ввода пользователя (мышь, клавиатура) во всей сессии: по нему замер понимает, что его «трогали».
internal static class UserInputMonitor
{
    public static uint LastInputTick()
    {
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            return GetLastInputInfo(ref info) ? info.Time : 0u;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0u;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

internal static class ResourceTrend
{
    // Подозрение на утечку требует и заметного наклона, и реального прироста за период после разогрева.
    private const double PrivateSlopeLimitMiBPerHour = 30, PrivateGrowthLimitMiB = 40;
    private const double HandlesSlopeLimit = 40, HandlesGrowthLimit = 100;
    private const double GdiSlopeLimit = 20, GdiGrowthLimit = 50;
    private const double UserSlopeLimit = 10, UserGrowthLimit = 25;
    private const double MinimumTrendMinutes = 20;

    // Наклон линии наименьших квадратов, единиц в час; нужен хотя бы три измерения с разным временем.
    public static double SlopePerHour(IReadOnlyList<ResourceSample> samples, Func<ResourceSample, double> value)
    {
        if (samples.Count < 3) return 0.0;

        double meanX = samples.Average(sample => sample.Minute);
        double meanY = samples.Average(value);
        double numerator = 0.0, denominator = 0.0;
        foreach (ResourceSample sample in samples)
        {
            double dx = sample.Minute - meanX;
            numerator += dx * (value(sample) - meanY);
            denominator += dx * dx;
        }

        return denominator <= 0.0 ? 0.0 : numerator / denominator * 60.0;
    }

    public static ResourceSummary Summarize(IReadOnlyList<ResourceSample> samples, double warmupMinutes)
    {
        if (samples.Count == 0)
            return new ResourceSummary(0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        List<ResourceSample> steady = samples.Where(sample => sample.Minute >= warmupMinutes).ToList();
        bool reliable = steady.Count >= 3 && steady[^1].Minute - steady[0].Minute >= MinimumTrendMinutes;
        if (steady.Count < 3) steady = samples.ToList();

        ResourceSample first = steady[0];
        ResourceSample last = steady[^1];
        double[] cpu = samples.Skip(1).Select(sample => sample.CpuPercentOfOneCore).OrderBy(value => value).ToArray();
        if (cpu.Length == 0) cpu = new[] { samples[0].CpuPercentOfOneCore };

        return new ResourceSummary(
            samples.Count,
            samples[^1].Minute - samples[0].Minute,
            reliable,
            cpu.Average(),
            cpu[Math.Min(cpu.Length - 1, Math.Max(0, (int)Math.Ceiling(0.95 * cpu.Length) - 1))],
            cpu[^1],
            first.PrivateMiB,
            last.PrivateMiB,
            samples.Max(sample => sample.PrivateMiB),
            last.ManagedMiB,
            SlopePerHour(steady, sample => sample.PrivateMiB),
            SlopePerHour(steady, sample => sample.Handles),
            SlopePerHour(steady, sample => sample.GdiObjects),
            SlopePerHour(steady, sample => sample.UserObjects),
            last.PrivateMiB - first.PrivateMiB,
            last.Handles - first.Handles,
            last.GdiObjects - first.GdiObjects,
            last.UserObjects - first.UserObjects,
            last.Handles,
            last.Threads,
            last.GdiObjects,
            last.UserObjects,
            last.Gen2Collections - first.Gen2Collections,
            samples[^1].TrackChanges,
            Math.Max(0.0, (samples[^1].IoWriteMiB - samples[0].IoWriteMiB) * 1024.0));
    }

    public static ResourceAssessment Assess(ResourceSummary summary)
    {
        if (!summary.TrendReliable)
            return new ResourceAssessment(ResourceVerdict.NotEnoughData, Array.Empty<string>());

        var reasons = new List<string>();
        if (summary.PrivateSlopePerHour > PrivateSlopeLimitMiBPerHour && summary.PrivateGrowthMiB > PrivateGrowthLimitMiB)
            reasons.Add(LocalizationService.Format("Закрытая память растёт на {0} МиБ в час", Format(summary.PrivateSlopePerHour)));
        if (summary.HandlesSlopePerHour > HandlesSlopeLimit && summary.HandlesGrowth > HandlesGrowthLimit)
            reasons.Add(LocalizationService.Format("Число дескрипторов растёт на {0} в час", Format(summary.HandlesSlopePerHour)));
        if (summary.GdiSlopePerHour > GdiSlopeLimit && summary.GdiGrowth > GdiGrowthLimit)
            reasons.Add(LocalizationService.Format("Число GDI-объектов растёт на {0} в час", Format(summary.GdiSlopePerHour)));
        if (summary.UserSlopePerHour > UserSlopeLimit && summary.UserGrowth > UserGrowthLimit)
            reasons.Add(LocalizationService.Format("Число USER-объектов растёт на {0} в час", Format(summary.UserSlopePerHour)));

        return new ResourceAssessment(reasons.Count > 0 ? ResourceVerdict.SuspectedLeak : ResourceVerdict.Normal, reasons);
    }

    public static string Format(double value) => value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
}

internal static class ResourceReport
{
    public static string Build(string title, IReadOnlyList<ResourceSample> samples, double warmupMinutes, bool loadMode, int inputDetections = 0)
    {
        ResourceSummary summary = ResourceTrend.Summarize(samples, warmupMinutes);
        ResourceAssessment assessment = ResourceTrend.Assess(summary);
        string F(double value) => ResourceTrend.Format(value);

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine(LocalizationService.Format("Версия: {0}", UpdateChecker.GetCurrentVersion()));
        text.AppendLine(LocalizationService.Format("Длительность: {0} мин, измерений: {1}", F(summary.DurationMinutes), summary.SampleCount));
        text.AppendLine();
        text.AppendLine(LocalizationService.Format("Процессор: в среднем {0}% ядра, 95-й процентиль {1}%, максимум {2}%",
            F(summary.CpuAverage), F(summary.CpuP95), F(summary.CpuMax)));
        text.AppendLine(LocalizationService.Format("Закрытая память: {0} МиБ в начале, {1} МиБ в конце, пик {2} МиБ",
            F(summary.PrivateStartMiB), F(summary.PrivateEndMiB), F(summary.PrivatePeakMiB)));
        text.AppendLine(LocalizationService.Format("Управляемая куча: {0} МиБ, сборок мусора 2-го поколения за период: {1}",
            F(summary.ManagedEndMiB), summary.Gen2Delta));
        text.AppendLine(LocalizationService.Format("Дескрипторы: {0}, потоки: {1}, GDI-объекты: {2}, USER-объекты: {3}",
            summary.Handles, summary.Threads, summary.GdiObjects, summary.UserObjects));
        text.AppendLine(LocalizationService.Format("Записано на диск: {0} КиБ", F(summary.DiskWrittenKiB)));
        if (summary.TrackChanges > 0)
            text.AppendLine(LocalizationService.Format("Смен трека за проверку: {0}", summary.TrackChanges));
        if (inputDetections > 0)
        {
            text.AppendLine();
            text.AppendLine(LocalizationService.Format("Внимание: во время замера обнаружен ввод пользователя (мышь или клавиатура), раз: {0}. Результат мог исказиться; повторите замер, не трогая мышь и клавиатуру.", inputDetections));
        }

        if (!loadMode && summary.TrendReliable)
        {
            text.AppendLine();
            text.AppendLine(LocalizationService.Format("Рост закрытой памяти после разогрева: {0} МиБ в час", F(summary.PrivateSlopePerHour)));
            text.AppendLine(LocalizationService.Format("Рост за час: дескрипторы {0}, GDI {1}, USER {2}",
                F(summary.HandlesSlopePerHour), F(summary.GdiSlopePerHour), F(summary.UserSlopePerHour)));
        }

        text.AppendLine();
        if (loadMode)
        {
            text.AppendLine(LocalizationService.Translate("Ориентир: при воспроизведении в главном окне около 3–6% ядра, на паузе меньше 1%."));
            return text.ToString();
        }

        switch (assessment.Verdict)
        {
            case ResourceVerdict.SuspectedLeak:
                text.AppendLine(LocalizationService.Translate("Вывод: возможна утечка"));
                foreach (string reason in assessment.Reasons) text.AppendLine("  - " + reason);
                break;
            case ResourceVerdict.Normal:
                text.AppendLine(LocalizationService.Translate("Вывод: признаков утечки нет"));
                break;
            default:
                text.AppendLine(LocalizationService.Translate("Вывод: для оценки утечек данных пока мало (нужно не менее 20 минут после разогрева)"));
                break;
        }

        return text.ToString();
    }

    public static string BuildCsv(IReadOnlyList<ResourceSample> samples)
    {
        var text = new StringBuilder();
        text.AppendLine("Minute,CpuPercentOfOneCore,PrivateMiB,WorkingSetMiB,ManagedMiB,Handles,Threads,GdiObjects,UserObjects,Gen2Collections,TrackChanges");
        foreach (ResourceSample s in samples)
        {
            text.AppendLine(string.Join(",",
                s.Minute.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                s.CpuPercentOfOneCore.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                s.PrivateMiB.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                s.WorkingSetMiB.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                s.ManagedMiB.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                s.Handles, s.Threads, s.GdiObjects, s.UserObjects, s.Gen2Collections, s.TrackChanges));
        }

        return text.ToString();
    }

    // Отчёт и ряд измерений лежат в %AppData%\\Lumisense\\diagnostics, отдельно от журнала.
    public static string Save(string report, IReadOnlyList<ResourceSample> samples)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumisense", "diagnostics");
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, "diagnostics_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        File.WriteAllText(stem + ".txt", report, Encoding.UTF8);
        File.WriteAllText(stem + ".csv", BuildCsv(samples), Encoding.UTF8);
        return directory;
    }
}

// Одна проверка: измеряет ресурсы заданное время и по желанию сама переключает треки, чтобы набрать много смен без участия человека.
internal sealed class ResourceSession
{
    public required string Title { get; init; }
    public required TimeSpan Duration { get; init; }
    public required TimeSpan SampleInterval { get; init; }
    public TimeSpan NextTrackInterval { get; init; } = TimeSpan.Zero;
    public double WarmupMinutes { get; init; }
    public bool LoadMode { get; init; }

    // Следить за вводом пользователя (мышь, клавиатура) и считать, сколько раз он случился за время замера.
    public bool TrackUserInput { get; init; }

    public int InputDetections { get; private set; }

    public async Task<IReadOnlyList<ResourceSample>> RunAsync(
        Action? nextTrack, IProgress<(double Fraction, ResourceSample Last)>? progress, CancellationToken token)
    {
        var samples = new List<ResourceSample>();
        using var sampler = new ResourceSampler();
        var clock = Stopwatch.StartNew();
        TimeSpan nextSample = TimeSpan.Zero;
        TimeSpan nextSwitch = NextTrackInterval > TimeSpan.Zero ? NextTrackInterval : TimeSpan.MaxValue;
        int trackChanges = 0;
        uint lastInput = TrackUserInput ? UserInputMonitor.LastInputTick() : 0u;
        InputDetections = 0;

        try
        {
            while (clock.Elapsed < Duration)
            {
                token.ThrowIfCancellationRequested();

                if (TrackUserInput)
                {
                    uint input = UserInputMonitor.LastInputTick();
                    if (input != lastInput && lastInput != 0u) InputDetections++;
                    lastInput = input;
                }

                if (clock.Elapsed >= nextSwitch)
                {
                    nextTrack?.Invoke();
                    trackChanges++;
                    nextSwitch += NextTrackInterval;
                }

                if (clock.Elapsed >= nextSample)
                {
                    ResourceSample sample = sampler.Read(trackChanges);
                    samples.Add(sample);
                    progress?.Report((Math.Min(1.0, clock.Elapsed / Duration), sample));
                    nextSample += SampleInterval;
                }

                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка пользователем: возвращаем то, что успели измерить.
        }

        return samples;
    }
}

// Постоянное лёгкое наблюдение: одно измерение в минуту (доли миллисекунды), последние сутки в памяти. Если за долгую работу
// память или число объектов заметно растёт, об этом пишется предупреждение в журнал; сам плеер при этом ничего не меняет.
internal sealed class ResourceWatchdog : IDisposable
{
    private const int MaxSamples = 24 * 60;
    private const double WarmupMinutes = 30;
    private static readonly TimeSpan WarnInterval = TimeSpan.FromHours(1);

    private readonly ResourceSampler _sampler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly List<ResourceSample> _samples = new();
    private DateTime _lastWarningUtc = DateTime.MinValue;

    public static ResourceWatchdog? Current { get; private set; }

    public static void Start()
    {
        Current ??= new ResourceWatchdog();
        Current._timer.Start();
    }

    private ResourceWatchdog()
    {
        _timer.Tick += (_, _) => Tick();
        _samples.Add(_sampler.Read(0));
    }

    public IReadOnlyList<ResourceSample> Samples
    {
        get
        {
            lock (_samples) return _samples.ToList();
        }
    }

    public ResourceSample Latest()
    {
        lock (_samples) return _samples[^1];
    }

    private void Tick()
    {
        ResourceSample sample = _sampler.Read(0);
        lock (_samples)
        {
            _samples.Add(sample);
            if (_samples.Count > MaxSamples) _samples.RemoveRange(0, _samples.Count - MaxSamples);
        }

        if (DateTime.UtcNow - _lastWarningUtc < WarnInterval) return;

        ResourceAssessment assessment = ResourceTrend.Assess(ResourceTrend.Summarize(Samples, WarmupMinutes));
        if (assessment.Verdict != ResourceVerdict.SuspectedLeak) return;

        _lastWarningUtc = DateTime.UtcNow;
        Logger.Warn("Наблюдение за ресурсами: возможна утечка. " + string.Join("; ", assessment.Reasons));
    }

    public void Dispose()
    {
        _timer.Stop();
        _sampler.Dispose();
    }
}
