using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lumisense;

// Windows группирует кнопки панели задач по AppUserModelID. У основного окна и мини-плеера
// общий идентификатор приложения, но открытые Settings должны сохранять собственный значок
// lumisense-settings.ico даже когда главное окно скрыто в режиме мини-плеера. Идентификатор
// задаётся только конкретному HWND Settings — процесс и остальные окна не затрагиваются.
internal static class TaskbarWindowIdentity
{
    public const string Settings = "Wasssly.Lumisense.Settings";

    private static readonly Guid IidIPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static void AssignWhenSourceReady(Window window, string appUserModelId)
    {
        // WPF Window не предоставляет IsSourceInitialized. Ненулевой Handle означает, что
        // окно уже получило HWND; иначе ждём штатное событие SourceInitialized.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Assign(window, appUserModelId);
            return;
        }

        EventHandler? sourceInitialized = null;
        sourceInitialized = (_, _) =>
        {
            window.SourceInitialized -= sourceInitialized;
            Assign(window, appUserModelId);
        };
        window.SourceInitialized += sourceInitialized;
    }

    private static void Assign(Window window, string appUserModelId)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        Guid propertyStoreInterfaceId = IidIPropertyStore;
        int result = SHGetPropertyStoreForWindow(hwnd, ref propertyStoreInterfaceId, out IPropertyStore propertyStore);
        if (result < 0) return;

        try
        {
            PropertyKey key = AppUserModelIdKey;
            PropVariant value = PropVariant.FromString(appUserModelId);
            try
            {
                if (propertyStore.SetValue(ref key, ref value) >= 0)
                    propertyStore.Commit();
            }
            finally
            {
                value.Dispose();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(propertyStore);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant : IDisposable
    {
        private const ushort VT_LPWSTR = 31;

        private ushort _valueType;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private IntPtr _pointerValue;

        public static PropVariant FromString(string value) => new()
        {
            _valueType = VT_LPWSTR,
            _pointerValue = Marshal.StringToCoTaskMemUni(value)
        };

        public void Dispose()
        {
            if (_pointerValue == IntPtr.Zero) return;

            Marshal.FreeCoTaskMem(_pointerValue);
            _pointerValue = IntPtr.Zero;
        }
    }
}
