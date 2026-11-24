using System.Windows.Media;

namespace Lumisense;

// Пружина в терминах CSS-токенов (stiffness и damping): SpringValue хранит коэффициент демпфирования,
// поэтому здесь значения из tokens.css переводятся в него одной формулой (так же сделано в IslandPlayerWindow.Tune).
internal readonly record struct SpringSpec(double Stiffness, double Damping)
{
    public double DampingRatio => Damping / (2 * Math.Sqrt(Stiffness));

    public void ApplyTo(SpringValue spring)
    {
        spring.Stiffness = Stiffness;
        spring.DampingRatio = DampingRatio;
    }
}

// Пружины компонентов designbookmark.com/motion: значения «stiffness damping» скопированы из их исходников и shared/tokens.css.
// Движок компонентов считает x'' = -k(x-t) - c·x' с единичной массой, SpringValue — то же самое через коэффициент демпфирования.
internal static class MotionTokens
{
    // Toggle switch: позиция кружка и «растяжение» (пинок скоростью), Checkbox list: заливка, отскок, галочка.
    public static readonly SpringSpec TogglePosition = new(320, 18);
    public static readonly SpringSpec ToggleStretch = new(300, 20);
    public const double ToggleStretchKick = 60;
    public static readonly SpringSpec CheckFill = new(300, 24);
    public static readonly SpringSpec CheckPop = new(520, 13);
    public static readonly SpringSpec CheckDraw = new(160, 20);

    // Spring dropdown.
    public static readonly SpringSpec DropdownHeight = new(300, 23);
    public static readonly SpringSpec DropdownFade = new(400, 34);
    public static readonly SpringSpec DropdownRow = new(300, 24);
    public static readonly SpringSpec DropdownTick = new(260, 26);
    public static readonly SpringSpec DropdownChevron = new(300, 18);
    public static readonly SpringSpec DropdownTrigger = new(500, 18);
    public static readonly SpringSpec DropdownValue = new(340, 26);
    public static readonly SpringSpec DropdownHighlightLead = new(620, 36);
    public static readonly SpringSpec DropdownHighlightTrail = new(200, 20);
    public static readonly SpringSpec DropdownHighlightFade = new(400, 32);

    // Word roll: пружина каждой буквы (1 — под полосой, 0 — на месте, -1 — над ней).
    public static readonly SpringSpec WordRollLetter = new(420, 26);

    // Rubber dock: края пилюли — две отдельные пружины; передний край жёсткий, задний ленивый, поэтому пилюля тянется как резина.
    public static readonly SpringSpec RubberLead = new(640, 38);
    public static readonly SpringSpec RubberTrail = new(150, 16);
    public static readonly SpringSpec RubberIdle = new(600, 44);

    // Read along: передний край пилюли быстрый, задний ленивый; вертикаль и прозрачность — отдельные пружины.
    public static readonly SpringSpec ReadAlongFront = new(520, 40);
    public static readonly SpringSpec ReadAlongBack = new(210, 26);
    public static readonly SpringSpec ReadAlongLine = new(600, 48);
    public static readonly SpringSpec ReadAlongFade = new(300, 30);

    // Dynamic menu.
    public static readonly SpringSpec MenuWidth = new(320, 26);
    public static readonly SpringSpec MenuHeight = new(240, 22);
    public static readonly SpringSpec MenuCross = new(380, 24);
    public static readonly SpringSpec MenuItem = new(260, 22);
}

// Набор пружин, которые двигаются вместе и применяют значения одним вызовом.
internal sealed class SpringTrack
{
    private readonly SpringValue[] _springs;
    private readonly Action _apply;

    // Пока возвращает true, трек считается активным, даже если все пружины стоят (нужно для отложенных стартов).
    public Func<bool>? KeepAlive { get; init; }

    // Вызывается один раз, когда трек остановился.
    public Action? Settled { get; init; }

    public SpringTrack(Action apply, params SpringValue[] springs)
    {
        _apply = apply;
        _springs = springs;
    }

    public bool Step(double deltaSeconds)
    {
        bool moving = false;
        foreach (SpringValue spring in _springs) moving |= spring.Step(deltaSeconds);
        _apply();
        return moving || KeepAlive?.Invoke() == true;
    }

    public void Finish()
    {
        foreach (SpringValue spring in _springs)
        {
            spring.Value = spring.Target;
            spring.Velocity = 0;
        }
        _apply();
        Settled?.Invoke();
    }
}

// Один общий подписчик CompositionTarget.Rendering на все пружинные анимации интерфейса: подписка живёт, только пока есть
// движущиеся треки.
internal static class SpringRunner
{
    private static readonly List<SpringTrack> Active = [];
    private static TimeSpan _lastFrame = TimeSpan.MinValue;
    private static bool _hooked;

    public static void Start(SpringTrack track)
    {
        if (!Active.Contains(track)) Active.Add(track);
        if (_hooked) return;

        _hooked = true;
        _lastFrame = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnFrame;
    }

    public static void Stop(SpringTrack track, bool finish)
    {
        if (!Active.Remove(track)) return;
        if (finish) track.Finish();
        Unhook();
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastFrame) return;

        double delta = _lastFrame == TimeSpan.MinValue ? 1.0 / 60 : (args.RenderingTime - _lastFrame).TotalSeconds;
        _lastFrame = args.RenderingTime;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            if (i >= Active.Count) continue;
            SpringTrack track = Active[i];
            if (track.Step(delta)) continue;

            Active.Remove(track);
            track.Settled?.Invoke();
        }

        Unhook();
    }

    private static void Unhook()
    {
        if (!_hooked || Active.Count > 0) return;

        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
        _lastFrame = TimeSpan.MinValue;
    }
}
