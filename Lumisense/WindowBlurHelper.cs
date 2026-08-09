using System.Runtime.InteropServices;

namespace AudioPlayer;

// "Blur" — третий вариант подложки окна (см. AppSettings.WindowBackdropType) рядом с Mica и
// Acrylic. В отличие от них — это не современный системный backdrop (DWM API, появился в
// Windows 11 и есть готовым в Wpf.Ui.Controls.WindowBackdropType) — а старая техника через
// недокументированный, но давно стабильный и широко используемый SetWindowCompositionAttribute
// (тот же механизм, что раньше давал классический Aero Glass, а затем — Acrylic в Windows 10 до
// появления системного backdrop). Работает и на Windows 10, и на Windows 11.
//
// ВАЖНО: используется именно ACCENT_ENABLE_ACRYLICBLURBEHIND (4), а не "простой"
// ACCENT_ENABLE_BLURBEHIND (3) — второй на современных сборках Windows 10/11 фактически не
// размывает вообще, а просто закрашивает окно сплошным полупрозрачным цветом (ровно то, что
// выглядело как "просто тёмный фон без размытия"). Настоящее размытие сейчас даёт только
// acrylic-вариант, и ему обязательно нужен непрозрачный (с ненулевым альфа-каналом)
// GradientColor — с нулевым/прозрачным цветом акриловый режим тоже не размывает.
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

    // Включает акриловое размытие фона за окном. Вызывать уже после того, как у окна отключён
    // современный системный backdrop (см. MainWindow.ApplyWindowBackdrop — WindowBackdropType.
    // None) — оба механизма одновременно не нужны и потенциально конфликтуют за одну и ту же
    // область композиции окна.
    //
    // isLightTheme — лёгкий сероватый оттенок поверх размытия подбирается под текущую тему
    // (тёмный/светлый), как и полагается акриловому эффекту — просто прозрачное "стекло" без
    // всякого оттенка на acrylic-режиме обычно выглядит грязно/неровно, лёгкий тон нужен для
    // читаемости содержимого поверх размытого фона.
    public static void EnableBlur(IntPtr hwnd, bool isLightTheme)
    {
        if (hwnd == IntPtr.Zero) return;

        // GradientColor — 0xAABBGGRR (обратный порядок байт по сравнению с привычным ARGB) для
        // этого конкретного API. R=G=B здесь специально — нейтральный серый одинаково выглядит
        // независимо от порядка байт, так что перепутать канал случайно негде.
        byte gray = isLightTheme ? (byte)0xEC : (byte)0x1E;
        const byte alpha = 0xB0; // ~69% непрозрачности тонировки — акрилу нужен ненулевой альфа-канал, иначе размытия не будет вовсе
        int gradientColor = (alpha << 24) | (gray << 16) | (gray << 8) | gray;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = gradientColor
        };
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

    // Выключает размытие — вызывается при переключении на Mica/Acrylic (или в любой другой
    // момент, когда окну снова нужен обычный непрозрачный/системный фон), чтобы не остался
    // "залипший" акриловый эффект поверх уже включённого системного backdrop.
    public static void DisableBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var accent = new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED };
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
