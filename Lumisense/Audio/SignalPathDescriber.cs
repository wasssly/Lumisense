using System.Globalization;

namespace Lumisense;

internal sealed record SignalPathState(
    int? SourceSampleRate,
    int? SourceChannels,
    string? Extension,
    bool Exclusive,
    string? OutputFormat,
    bool TempoOrPitch,
    bool ReplayGain,
    bool Equalizer,
    bool Mono,
    bool Balance,
    bool VolumeScaled,
    bool Limiter,
    bool StereoImage = false,
    bool Loudness = false);

internal sealed record SignalPathDescription(string Text, bool IsBitPerfect);

// «Bit-perfect» только при выключенной обработке и Exclusive на частоте файла: громкость системы плеер не контролирует.
internal static class SignalPathDescriber
{
    public static SignalPathDescription Describe(SignalPathState state, bool localized)
    {
        string T(string russian, string english) => localized ? LocalizationService.Translate(russian) : english;

        var stages = new System.Collections.Generic.List<string>();
        string source = state.SourceSampleRate is int rate
            ? $"{(string.IsNullOrEmpty(state.Extension) ? T("Файл", "File") : state.Extension.TrimStart('.').ToUpperInvariant())} {FormatRate(rate)}"
                + (state.SourceChannels is int channels ? $" · {channels} ch" : "")
            : T("Файл", "File");
        stages.Add(source);

        if (state.TempoOrPitch) stages.Add(T("Темп/тон", "Tempo/pitch"));
        if (state.ReplayGain) stages.Add("ReplayGain");
        if (state.Equalizer) stages.Add(T("Эквалайзер", "Equalizer"));
        if (state.Mono) stages.Add(T("Моно", "Mono"));
        if (state.Balance) stages.Add(T("Баланс", "Balance"));
        if (state.StereoImage) stages.Add(T("Стереобаза", "Stereo image"));
        if (state.Loudness) stages.Add(T("Тонкомпенсация", "Loudness"));
        if (state.VolumeScaled) stages.Add(T("Громкость", "Volume"));
        if (state.Limiter) stages.Add(T("Лимитер (без вмешательства, пока нет пиков)", "Limiter (idle unless peaks)"));

        string mode = state.Exclusive ? "WASAPI Exclusive" : "WASAPI Shared";
        stages.Add(string.IsNullOrEmpty(state.OutputFormat) ? mode : $"{mode} {state.OutputFormat}");

        bool neutral = !state.TempoOrPitch && !state.ReplayGain && !state.Equalizer && !state.Mono && !state.Balance && !state.VolumeScaled && !state.StereoImage && !state.Loudness;
        bool sameRate = state.SourceSampleRate is int sourceRate
            && state.OutputFormat is { Length: > 0 } format
            && format.StartsWith(FormatRate(sourceRate), System.StringComparison.Ordinal);
        bool bitPerfect = neutral && state.Exclusive && sameRate;

        string text = string.Join(" → ", stages);
        if (bitPerfect)
            text += "\n" + T("Без обработки: сигнал идёт в Exclusive на частоте файла (bit-perfect).", "No processing: the signal goes to Exclusive at the file sample rate (bit-perfect).");
        return new SignalPathDescription(text, bitPerfect);
    }

    private static string FormatRate(int sampleRate) =>
        string.Create(CultureInfo.InvariantCulture, $"{sampleRate / 1000.0:0.#} kHz");
}
