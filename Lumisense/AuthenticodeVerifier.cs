using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AudioPlayer;

internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_PROV_FLAGS_REVOCATION_CHECK_END_CERT = 0x80;

    public static bool IsValid(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return false;

        IntPtr filePathPtr = IntPtr.Zero;
        try
        {
            filePathPtr = Marshal.StringToCoTaskMemUni(filePath);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPtr,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero
            };

            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WTD_UI_NONE,
                RevocationChecks = WTD_REVOKE_NONE,
                UnionChoice = WTD_CHOICE_FILE,
                FileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>()),
                StateAction = WTD_STATEACTION_IGNORE,
                ProviderFlags = WTD_PROV_FLAGS_REVOCATION_CHECK_END_CERT
            };

            try
            {
                Marshal.StructureToPtr(fileInfo, trustData.FileInfo, false);
                var actionIdentifier = GenericVerifyV2;
                uint status = WinVerifyTrust(IntPtr.Zero, ref actionIdentifier, ref trustData);
                if (status != 0) return false;

                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
                string signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
                return signer.Contains("Lumisense", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Marshal.FreeCoTaskMem(trustData.FileInfo);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (filePathPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionIdentifier,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProviderFlags;
        public uint UIContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }
}
