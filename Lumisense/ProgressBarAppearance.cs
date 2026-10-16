namespace Lumisense;

// Вид полосы воспроизведения: цвет, толщина, форма концов, волна Material, Waveform и подсказка при наведении.
// Значения по умолчанию воспроизводят прежний вид, множители (1.0) — это «как раньше» для каждой полосы.
// Один профиль может обслуживать все окна или у каждого окна свой (AppSettings.ProgressAppearanceScope).
public sealed class ProgressBarAppearance
{
    // "Accent" — акцент приложения (как раньше), "Cover" — цвет обложки текущего трека, "Custom" — ColorHex.
    public string ColorMode { get; set; } = "Accent";
    public string ColorHex { get; set; } = "#0078D4";

    // Множители к стандартным размерам каждой полосы, чтобы 1.0 везде означало прежний вид.
    public double TrackThickness { get; set; } = 1.0;
    public double ThumbSize { get; set; } = 1.0;

    // "Round" (как раньше) или "Flat": прямые концы дорожки, волны и столбиков Waveform.
    public string EndShape { get; set; } = "Round";

    // Волна Material: множители к высоте, длине и скорости; WaveAlways — волна бежит и на паузе.
    public double WaveAmplitude { get; set; } = 1.0;
    public double WaveLength { get; set; } = 1.0;
    public double WaveSpeed { get; set; } = 1.0;
    public bool WaveAlways { get; set; }

    // Ручка на волне Material: вертикальная пилюля на границе проигранной части (размер — ThumbSize).
    public bool WaveHandle { get; set; }

    // Waveform: доля деления под зазор между столбиками и прозрачность ещё не проигранной части.
    public double WaveformGap { get; set; } = 0.35;
    public double WaveformUnplayedOpacity { get; set; } = 1.0;

    // Подсказка при наведении на Waveform: время в точке и подсветка участка до курсора.
    public bool HoverTime { get; set; } = true;
    public bool HoverHighlight { get; set; } = true;

    public bool IsFlat => EndShape == "Flat";

    public ProgressBarAppearance Clone() => (ProgressBarAppearance)MemberwiseClone();

    // Приводит значения из файла настроек к допустимым: битый файл не должен ломать отрисовку полосы.
    public void Normalize()
    {
        ColorMode = ColorMode is "Accent" or "Cover" or "Custom" ? ColorMode : "Accent";
        ColorHex = ToastColorCalculator.TryParse(ColorHex) is null ? "#0078D4" : ColorHex;
        EndShape = EndShape is "Round" or "Flat" ? EndShape : "Round";

        TrackThickness = Clamp(TrackThickness, 0.5, 2.0, 1.0);
        ThumbSize = Clamp(ThumbSize, 0.6, 1.5, 1.0);
        WaveAmplitude = Clamp(WaveAmplitude, 0.0, 2.0, 1.0);
        WaveLength = Clamp(WaveLength, 0.5, 2.0, 1.0);
        WaveSpeed = Clamp(WaveSpeed, 0.0, 2.0, 1.0);
        WaveformGap = Clamp(WaveformGap, 0.1, 0.7, 0.35);
        WaveformUnplayedOpacity = Clamp(WaveformUnplayedOpacity, 0.3, 1.0, 1.0);
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
