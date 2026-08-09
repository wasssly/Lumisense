using System.Runtime.InteropServices;
using System.Windows.Media;

namespace AudioPlayer;

// "Blur" и "AccentBlur" — два родственных варианта подложки окна через один и тот же
// недокументированный, но давно стабильный и широко используемый механизм
// SetWindowCompositionAttribute (тот же, что раньше давал классический Aero Glass, а затем —
// Acrylic в Windows 10 до появления системного backdrop). Работает и на Windows 10, и на
// Windows 11 — в отличие от Mica/Acrylic из Wpf.Ui.Controls.WindowBackdropType (тот — уже
// современный системный DWM backdrop, появившийся в Windows 11).
//
// ВАЖНО: оба варианта используют именно ACCENT_ENABLE_ACRYLICBLURBEHIND (4), а не "простой"
// ACCENT_ENABLE_BLURBEHIND (3) — второй на современных сборках Windows 10/11 фактически не
// размывает вообще, а просто закрашивает окно сплошным полупрозрачным цветом (ровно то, что
// выглядело как "просто тёмный фон без размытия"). Настоящее размытие сейчас даёт только
// acrylic-вариант, и ему обязательно нужен непрозрачный (с ненулевым альфа-каналом)
// GradientColor — с нулевым/прозрачным цветом акриловый режим тоже не размывает. Разница между
// EnableBlur и EnableAccentBlur — только в том, ОТКУДА берётся тон тонировки: у первого это
// нейтральный серый под тему, у второго — акцентный цвет приложения (системный акцент Windows
// или выбранный вручную, см. MainWindow.GetResolvedAccentColor). Оба используют одну и ту же
// AccentPolicy/Win32-инфраструктуру — общий кусок вынесен в ApplyAcrylicBlur/ApplyAccentPolicy.
internal static class WindowBlurHelper
{
    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19,
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // ~69% непрозрачности тонировки — акрилу нужен ненулевой альфа-канал, иначе размытия не
    // будет вовсе. Общее значение для обычного и акцентного варианта — визуально они должны
    // отличаться тоном, а не степенью прозрачности.
    private const byte BlurAlpha = 0xB0;

    // Включает акриловое размытие фона за окном с нейтральной тонировкой под текущую тему —
    // исходный вариант "Blur" (см. AppSettings.WindowBackdropType == "Blur"). Вызывать уже
    // после того, как у окна отключён современный системный backdrop (см.
    // MainWindow.ApplyWindowBackdrop — WindowBackdropType.None) — оба механизма одновременно не
    // нужны и потенциально конфликтуют за одну и ту же область композиции окна.
    //
    // isLightTheme — лёгкий сероватый оттенок поверх размытия подбирается под текущую тему
    // (тёмный/светлый), как и полагается акриловому эффекту — просто прозрачное "стекло" без
    // всякого оттенка на acrylic-режиме обычно выглядит грязно/неровно, лёгкий тон нужен для
    // читаемости содержимого поверх размытого фона.
    public static void EnableBlur(IntPtr hwnd, bool isLightTheme)
    {
        byte gray = isLightTheme ? (byte)0xEC : (byte)0x1E;
        ApplyAcrylicBlur(hwnd, r: gray, g: gray, b: gray);
    }

    // "AccentBlurBehind" — альтернативная подложка (см. AppSettings.WindowBackdropType ==
    // "AccentBlur"): то же самое акриловое размытие, что и EnableBlur, но тонировка не
    // нейтрально-серая, а подмешивает акцентный цвет приложения — системный акцент Windows или
    // выбранный вручную в настройках (см. MainWindow.GetResolvedAccentColor). Даёт то самое
    // "размытие с акцентом на системные цвета Windows": фон окна еле заметно окрашен в тот же
    // тон, что и элементы управления, а не абсолютно нейтрален, как классический Blur.
    //
    // accentColor — уже разрешённый цвет (см. GetResolvedAccentColor), а не сырые
    // AccentColorMode/AccentColorHex из настроек: этому классу незачем знать про формат
    // настроек, он просто красит тем цветом, который ему передали.
    public static void EnableAccentBlur(IntPtr hwnd, Color accentColor, bool isLightTheme)
    {
        // Чистый акцент поверх размытия почти всегда слишком ярок и контрастен для фона целого
        // окна (акцентные цвета подбираются как раз для того, чтобы выделяться на фоне, а не
        // сливаться с ним) — поэтому не красим им напрямую, а подмешиваем в тот же базовый тон
        // темы, что использует обычный Blur, с сравнительно небольшим весом. AccentWeight
        // подобран на глаз: заметно чувствуется, что подложка "того же цвета", что и акцент
        // приложения, но фон остаётся достаточно нейтральным, чтобы поверх него было одинаково
        // хорошо читаемо и тёмным, и светлым текстом интерфейса.
        const double accentWeight = 0.30;
        byte baseTone = isLightTheme ? (byte)0xEC : (byte)0x1E;

        byte Blend(byte accentChannel) =>
            (byte)Math.Clamp(Math.Round(accentChannel * accentWeight + baseTone * (1 - accentWeight)), 0, 255);

        ApplyAcrylicBlur(hwnd, r: Blend(accentColor.R), g: Blend(accentColor.G), b: Blend(accentColor.B));
    }

    // Общая часть EnableBlur/EnableAccentBlur — оба в итоге просто по-разному считают тон
    // тонировки, а сам вызов Win32 API и структура AccentPolicy у них идентичны.
    private static void ApplyAcrylicBlur(IntPtr hwnd, byte r, byte g, byte b)
    {
        if (hwnd == IntPtr.Zero) return;

        // GradientColor — 0xAABBGGRR (обратный порядок байт по сравнению с привычным ARGB) для
        // этого конкретного API.
        int gradientColor = (BlurAlpha << 24) | (b << 16) | (g << 8) | r;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = gradientColor
        };

        ApplyAccentPolicy(hwnd, accent);
    }

    // Выключает размытие (обычное или акцентное — оба выключаются одинаково) — вызывается при
    // переключении на Mica/Acrylic (или в любой другой момент, когда окну снова нужен обычный
    // непрозрачный/системный фон), чтобы не остался "залипший" акриловый эффект поверх уже
    // включённого системного backdrop.
    public static void DisableBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        ApplyAccentPolicy(hwnd, new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED });
    }

    private static void ApplyAccentPolicy(IntPtr hwnd, AccentPolicy accent)
    {
        int accentSize = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);

        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = accentSize,
                Data = accentPtr
            };

            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }
}
