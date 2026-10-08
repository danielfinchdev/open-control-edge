using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OpenControlEdge.Services;

/// Authenticode checks (WinVerifyTrust: the file's digest and trust chain; the caller pins the signer, as PawnIoInstaller
/// does) and the version resource of an update's executable. Never throws.
internal static class AuthenticodeVerifier
{
    /// The version resource says Open Control Edge and exactly this version (no signature involved).
    internal static bool DeclaresVersion(string path, Version version)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return info.ProductName == "Open Control Edge"
                   && new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) == Normalize(version);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    /// The signer's certificate when Windows trusts the file's Authenticode signature; null otherwise.
    internal static X509Certificate2? TrustedSigner(string path)
    {
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        IntPtr dataPointer = IntPtr.Zero;
        bool fileMarshalled = false;
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo(path), filePointer, false);
            fileMarshalled = true;
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(new WinTrustData { FileInfo = filePointer }, dataPointer, false);
            Guid action = WintrustActionGenericVerifyV2;
            if (WinVerifyTrust(IntPtr.Zero, ref action, dataPointer) != 0) return null;
#pragma warning disable SYSLIB0057 // The certificate embedded in a signed file, not one loaded from a certificate file.
            return new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException or IOException)
        {
            return null;
        }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            if (fileMarshalled) Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    /// The common name ("CN", OID 2.5.4.3) or organization ("O", 2.5.4.10) of the certificate's subject, exactly as
    /// written; null when absent or ambiguous.
    internal static string? NamePart(X509Certificate2 certificate, string attribute)
    {
        string oid = attribute switch { "CN" => "2.5.4.3", "O" => "2.5.4.10", _ => throw new ArgumentOutOfRangeException(nameof(attribute)) };
        string? found = null;
        foreach (X500RelativeDistinguishedName part in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (part.HasMultipleElements || part.GetSingleElementType().Value != oid) continue;
            if (found is not null) return null;
            found = part.GetSingleElementValue();
        }
        return found;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    /// Revocation data only from the local cache: the check never waits on the network.
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

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

        public WinTrustData()
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = SipClientData = StateData = UrlReference = FileInfo = IntPtr.Zero;
            UiChoice = WTD_UI_NONE;
            RevocationChecks = WTD_REVOKE_NONE;
            UnionChoice = WTD_CHOICE_FILE;
            StateAction = WTD_STATEACTION_IGNORE;
            ProviderFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
            UiContext = 0;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);
}
