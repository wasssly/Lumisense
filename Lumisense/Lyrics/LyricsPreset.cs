namespace Lumisense;

// Пресет оформления текста песни: все настройки страницы «Тексты песен», относящиеся к виду текста (не к поиску и источникам).
// Встроенные пресеты неизменяемы; свои сохраняются в AppSettings.CustomLyricsPresets под своим именем.
public sealed class LyricsPreset
{
    public string Name { get; set; } = string.Empty;
    public double SyncedLyricsFontSize { get; set; } = 14.0;
    public int NowPlayingLyricsFontSize { get; set; } = LyricsHighlightStyle.DefaultNowPlayingFontSize;
    public string SyncedLyricsHighlightEffect { get; set; } = "Glow";
    public int SyncedLyricsGlowStrength { get; set; } = LyricsHighlightStyle.DefaultGlowStrength;
    public bool SyncedLyricsScaleActiveLine { get; set; }
    public int SyncedLyricsScalePercent { get; set; } = 5;
    public bool SyncedLyricsFadeDistantLines { get; set; }
    public int SyncedLyricsFadeStrength { get; set; } = 50;
    public int SyncedLyricsVisibleLines { get; set; }
    public bool SyncedLyricsBlurInactive { get; set; }
    public int SyncedLyricsBlurStrength { get; set; } = LyricsHighlightStyle.DefaultBlurStrength;
    public string LyricsTextAlignment { get; set; } = "Left";
    public string SyncedLyricsTransition { get; set; } = LyricsTransitionStyle.Smooth;
    public string LyricsDisplayMode { get; set; } = "Synced";
    public bool HideCoverInLyricsPanel { get; set; } = true;

    public LyricsPreset Clone() => (LyricsPreset)MemberwiseClone();

    // Совпадают ли все значения оформления (имя не сравнивается).
    public bool HasSameValues(LyricsPreset other) =>
        SyncedLyricsFontSize == other.SyncedLyricsFontSize
        && NowPlayingLyricsFontSize == other.NowPlayingLyricsFontSize
        && SyncedLyricsHighlightEffect == other.SyncedLyricsHighlightEffect
        && SyncedLyricsGlowStrength == other.SyncedLyricsGlowStrength
        && SyncedLyricsScaleActiveLine == other.SyncedLyricsScaleActiveLine
        && SyncedLyricsScalePercent == other.SyncedLyricsScalePercent
        && SyncedLyricsFadeDistantLines == other.SyncedLyricsFadeDistantLines
        && SyncedLyricsFadeStrength == other.SyncedLyricsFadeStrength
        && LyricsHighlightStyle.NormalizeVisibleLines(SyncedLyricsVisibleLines) == LyricsHighlightStyle.NormalizeVisibleLines(other.SyncedLyricsVisibleLines)
        && SyncedLyricsBlurInactive == other.SyncedLyricsBlurInactive
        && SyncedLyricsBlurStrength == other.SyncedLyricsBlurStrength
        && LyricsTextAlignment == other.LyricsTextAlignment
        && LyricsTransitionStyle.Normalize(SyncedLyricsTransition) == LyricsTransitionStyle.Normalize(other.SyncedLyricsTransition)
        && LyricsDisplayMode == other.LyricsDisplayMode
        && HideCoverInLyricsPanel == other.HideCoverInLyricsPanel;

    public static LyricsPreset Capture(AppSettings settings, string name) => new()
    {
        Name = name,
        SyncedLyricsFontSize = settings.SyncedLyricsFontSize,
        NowPlayingLyricsFontSize = settings.NowPlayingLyricsFontSize,
        SyncedLyricsHighlightEffect = settings.SyncedLyricsHighlightEffect,
        SyncedLyricsGlowStrength = settings.SyncedLyricsGlowStrength,
        SyncedLyricsScaleActiveLine = settings.SyncedLyricsScaleActiveLine,
        SyncedLyricsScalePercent = settings.SyncedLyricsScalePercent,
        SyncedLyricsFadeDistantLines = settings.SyncedLyricsFadeDistantLines,
        SyncedLyricsFadeStrength = settings.SyncedLyricsFadeStrength,
        SyncedLyricsVisibleLines = settings.SyncedLyricsVisibleLines,
        SyncedLyricsBlurInactive = settings.SyncedLyricsBlurInactive,
        SyncedLyricsBlurStrength = settings.SyncedLyricsBlurStrength,
        LyricsTextAlignment = settings.LyricsTextAlignment,
        SyncedLyricsTransition = LyricsTransitionStyle.Normalize(settings.SyncedLyricsTransition),
        LyricsDisplayMode = settings.LyricsDisplayMode,
        HideCoverInLyricsPanel = settings.HideCoverInLyricsPanel,
    };

    public void ApplyTo(AppSettings settings)
    {
        settings.SyncedLyricsFontSize = Math.Clamp(SyncedLyricsFontSize, 12, 20);
        settings.NowPlayingLyricsFontSize = Math.Clamp(NowPlayingLyricsFontSize,
            LyricsHighlightStyle.MinNowPlayingFontSize, LyricsHighlightStyle.MaxNowPlayingFontSize);
        settings.SyncedLyricsHighlightEffect = SyncedLyricsHighlightEffect;
        settings.SyncedLyricsGlowStrength = Math.Clamp(SyncedLyricsGlowStrength, 0, 100);
        settings.SyncedLyricsScaleActiveLine = SyncedLyricsScaleActiveLine;
        settings.SyncedLyricsScalePercent = Math.Clamp(SyncedLyricsScalePercent,
            LyricsHighlightStyle.MinScalePercent, LyricsHighlightStyle.MaxScalePercent);
        settings.SyncedLyricsFadeDistantLines = SyncedLyricsFadeDistantLines;
        settings.SyncedLyricsFadeStrength = Math.Clamp(SyncedLyricsFadeStrength, 0, 100);
        settings.SyncedLyricsVisibleLines = LyricsHighlightStyle.NormalizeVisibleLines(SyncedLyricsVisibleLines);
        settings.SyncedLyricsBlurInactive = SyncedLyricsBlurInactive;
        settings.SyncedLyricsBlurStrength = Math.Clamp(SyncedLyricsBlurStrength,
            LyricsHighlightStyle.MinBlurStrength, LyricsHighlightStyle.MaxBlurStrength);
        settings.LyricsTextAlignment = LyricsTextAlignment;
        settings.SyncedLyricsTransition = LyricsTransitionStyle.Normalize(SyncedLyricsTransition);
        settings.LyricsDisplayMode = LyricsDisplayMode == "Plain" ? "Plain" : "Synced";
        settings.HideCoverInLyricsPanel = HideCoverInLyricsPanel;
        settings.LyricsPresetName = Name;
    }

    // Встроенные пресеты.
    public static IReadOnlyList<LyricsPreset> BuiltIn { get; } =
    [
        new LyricsPreset
        {
            Name = "Классика",
        },
        new LyricsPreset
        {
            // Как в Apple Music: крупный текст слева, активная строка больше и ярче, остальные приглушены и размыты.
            Name = "Apple Music",
            SyncedLyricsFontSize = 18,
            NowPlayingLyricsFontSize = 26,
            SyncedLyricsHighlightEffect = "None",
            SyncedLyricsScaleActiveLine = true,
            SyncedLyricsScalePercent = 8,
            SyncedLyricsFadeDistantLines = true,
            SyncedLyricsFadeStrength = 60,
            SyncedLyricsBlurInactive = true,
            SyncedLyricsBlurStrength = 5,
            LyricsTextAlignment = "Left",
            SyncedLyricsTransition = LyricsTransitionStyle.AppleMusic,
        },
        new LyricsPreset
        {
            // Караоке: по центру, свечение и подложка, три видимые строки.
            Name = "Караоке",
            SyncedLyricsFontSize = 16,
            NowPlayingLyricsFontSize = 24,
            SyncedLyricsHighlightEffect = "GlowPill",
            SyncedLyricsGlowStrength = 70,
            SyncedLyricsScaleActiveLine = true,
            SyncedLyricsScalePercent = 6,
            SyncedLyricsVisibleLines = 5,
            LyricsTextAlignment = "Center",
            SyncedLyricsTransition = LyricsTransitionStyle.Soft,
        },
        new LyricsPreset
        {
            // Минимализм: без эффектов и анимации, только смена цвета активной строки.
            Name = "Минимализм",
            SyncedLyricsHighlightEffect = "None",
            SyncedLyricsTransition = LyricsTransitionStyle.None,
        },
    ];

    public static bool IsBuiltInName(string? name) =>
        BuiltIn.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
}
