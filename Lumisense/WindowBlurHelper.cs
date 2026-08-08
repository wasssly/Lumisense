using System.Runtime.InteropServices;

namespace AudioPlayer;

// "Blur" — третий вариант подложки окна (см. AppSettings.WindowBackdropType) рядом с Mica и
// Acrylic. В отличие от них — это не современный системный backdrop (DWM API, появился в
// Windows 11 и есть готовым в Wpf.Ui.Controls.WindowBackdropType) — а старая техника
// "AccentBlurBehind" через недокументированный, но давно стабильный и широко используемый
// SetWindowCompositionAttribute (тот же механизм, что раньше давал классический Aero Glass, а
// затем — Acrylic в Windows 10 до появления системного backdrop). Работает и на Windows 10, и
// на Windows 11, независимо от версии/сборки — в этом смысле это самый "совместимый" из трёх
// вариантов подложки, хоть и визуально более простой (равномерное размытие без частиц и
// оттенка, которые даёт настоящий Acrylic).
internal static class WindowBlurHelper
{
    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_BLURBEHIND = 3,
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

    // Включает классическое размытие фона за окном. Вызывать уже после того, как у окна
    // отключён современный системный backdrop (см. MainWindow.ApplyWindowBackdrop —
    // WindowBackdropType.None) — оба механизма одновременно не нужны и потенциально
    // конфликтуют за одну и ту же область композиции окна.
    public static void EnableBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var accent = new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND };
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
    // "залипший" ACCENT_ENABLE_BLURBEHIND поверх уже включённого системного backdrop.
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
