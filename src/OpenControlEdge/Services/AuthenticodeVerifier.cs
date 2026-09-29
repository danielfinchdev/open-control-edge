using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OpenControlEdge.Services;

/// Verifies the PE digest and Windows trust chain, then checks the publisher identity.
internal static class AuthenticodeVerifier
{
    internal static bool IsValidSignPathSignature(string path)
    {
        var file = new WinTrustFileInfo(path);
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        IntPtr dataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData { FileInfo = filePointer };
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, false);
            Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(IntPtr.Zero, ref action, dataPointer) != 0) return false;
            using X509Certificate2 certificate = new(X509Certificate.CreateFromSignedFile(path));
            return certificate.Subject.Contains("SignPath Foundation", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint Size;
        internal string FilePath;
        internal IntPtr FileHandle, KnownSubject;
        internal WinTrustFileInfo(string path) { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(); FilePath = path; FileHandle = KnownSubject = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint Size;
        internal IntPtr PolicyCallbackData, SipClientData;
        internal uint UiChoice, RevocationChecks, UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData, UrlReference;
        internal uint ProviderFlags, UiContext;
        public WinTrustData() { Size = (uint)Marshal.SizeOf<WinTrustData>(); PolicyCallbackData = SipClientData = StateData = UrlReference = IntPtr.Zero; UiChoice = 2; RevocationChecks = 0; UnionChoice = 1; FileInfo = IntPtr.Zero; StateAction = 0; ProviderFlags = 0x1000; UiContext = 0; }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);
}
