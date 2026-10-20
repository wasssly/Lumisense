using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Lumisense;

// Основа окна «Размытие» (AppSettings.WindowBackdropType == "Blur"): обычное размытие позади окна, без тонировки и без дополнительных
// программ. Окно просит его у Windows теми же вызовами, которыми пользуются обычные приложения (SetWindowCompositionAttribute с
// ACCENT_ENABLE_BLURBEHIND и DwmEnableBlurBehindWindow).
// FluentWindow сначала выставляет Acrylic (прозрачный фон окна, поддерживается и в Windows 10); поверх него здесь накладывается размытие.
internal static class DwmBlurBackdrop
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int SystemBackdropMinimumBuild = 22621;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;
    private const int DwmBbEnable = 0x1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;
    private const uint RdwFrame = 0x0400;

    private sealed class State
    {
        public bool Wanted;
        public bool Applied;
        public bool NeedsActivationNudge;
    }

    private static readonly ConditionalWeakTable<Window, State> States = new();

    public static bool IsApplied(Window window) => States.TryGetValue(window, out State? state) && state.Applied;

    // Включает или выключает размытие для окна. Выключение (Clear) только снимает наш эффект; основу WPF-UI после него
    // должен выставить вызывающий (ApplyWindowBackdrop переключает тип через None).
    public static void Sync(Window window, bool enabled)
    {
        State state = States.GetOrCreateValue(window);
        state.Wanted = enabled;

        if (!enabled)
        {
            if (state.Applied) Clear(window);
            return;
        }

        bool wasApplied = state.Applied;
        ApplyIfPossible(window, state);
        if (!wasApplied && state.Applied) state.NeedsActivationNudge = true;

        // FluentWindow накладывает свою основу уже после SourceInitialized и при смене темы, поэтому наше размытие
        // повторяется ещё раз, когда окно простаивает, и при создании HWND.
        window.SourceInitialized -= OnSourceInitialized;
        window.SourceInitialized += OnSourceInitialized;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!state.Wanted) return;

            ApplyIfPossible(window, state);
            if (state.NeedsActivationNudge)
            {
                state.NeedsActivationNudge = false;
                ActivateBriefly(window);
            }
        }));
    }

    public static void Clear(Window window)
    {
        if (!States.TryGetValue(window, out State? state)) return;

        state.Wanted = false;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && state.Applied)
        {
            SetAccent(hwnd, AccentDisabled);
            var blur = new DwmBlurBehind { Flags = DwmBbEnable, Enable = false };
            DwmEnableBlurBehindWindow(hwnd, ref blur);
            Refresh(hwnd);
        }

        state.Applied = false;
    }

    // Windows показывает новое размытие неактивного окна только после его активации (главное окно, когда основу меняют из окна
    // настроек, обновлялось лишь после клика по нему). Поэтому видимое неактивное окно на миг активируется, и фокус сразу
    // возвращается окну, которое было активным; свёрнутые и спрятанные окна не трогаем, чтобы они не всплывали.
    private static void ActivateBriefly(Window window)
    {
        if (!window.IsVisible || window.WindowState == WindowState.Minimized || window.IsActive) return;

        Window? previous = Application.Current?.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.IsActive);
        if (previous is null || ReferenceEquals(previous, window)) return;

        window.Activate();
        previous.Activate();
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window && States.TryGetValue(window, out State? state) && state.Wanted)
            ApplyIfPossible(window, state);
    }

    private static void ApplyIfPossible(Window window, State state)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // Системная основа Windows 11 перекрывала бы размытие, поэтому её отключаем.
            if (Environment.OSVersion.Version.Build >= SystemBackdropMinimumBuild)
            {
                int none = DwmsbtNone;
                DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
            }

            SetAccent(hwnd, AccentEnableBlurBehind);
            var blur = new DwmBlurBehind { Flags = DwmBbEnable, Enable = true };
            DwmEnableBlurBehindWindow(hwnd, ref blur);
            state.Applied = true;
            Refresh(hwnd);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Warn($"Размытие окна не поддерживается: {ex.Message}");
        }
    }

    // Окно перерисовывается только по событию: без этого неактивное окно (главное, когда основу меняют из настроек) показывало
    // новое размытие лишь после клика по нему. SWP_FRAMECHANGED и RedrawWindow заставляют DWM пересчитать и показать эффект сразу.
    private static void Refresh(IntPtr hwnd)
    {
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwFrame | RdwAllChildren | RdwUpdateNow);
    }

    private static void SetAccent(IntPtr hwnd, int accentState)
    {
        var policy = new AccentPolicy { AccentState = accentState };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData { Attribute = WcaAccentPolicy, Data = buffer, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public int Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr BlurRegion;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);
}
