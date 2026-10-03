using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OpenControlEdge.Services;

/// Authenticode checks: Windows verifies the file's digest and trust chain (WinVerifyTrust), then the caller decides
/// whether the signer is the one it expects. Never throws.
internal static class AuthenticodeVerifier
{
    /// The publisher of the official releases. SignPath Foundation signs many open-source projects with this same
    /// identity, so the signer alone does not prove the file is Open Control Edge: see IsOfficialExecutable.
    private const string ReleaseSigner = "SignPath Foundation";

    /// Publishers accepted for the native libraries of an update, as (CN, O): the WPF libraries of the .NET runtime,
    /// the Mono.Posix helpers and anything the release signs itself.
    private static readonly (string CommonName, string Organization)[] LibrarySigners =
    {
        (".NET", "Microsoft Corporation"),
        ("Microsoft Windows", "Microsoft Corporation"),
        ("Microsoft Corporation", "Microsoft Corporation"),
        ("Xamarin Inc.", "Xamarin Inc."),
        (ReleaseSigner, ReleaseSigner),
    };

    /// An Open Control Edge executable of exactly this version, signed for the official releases: trusted signature
    /// whose signer is SignPath Foundation (exact name), and a version resource that says Open Control Edge and version.
    internal static bool IsOfficialExecutable(string path, Version version)
    {
        using X509Certificate2? signer = TrustedSigner(path);
        if (signer is null || NamePart(signer, "CN") != ReleaseSigner) return false;
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

    /// A native library signed by one of LibrarySigners.
    internal static bool IsTrustedLibrary(string path)
    {
        using X509Certificate2? signer = TrustedSigner(path);
        if (signer is null) return false;
        string? commonName = NamePart(signer, "CN"), organization = NamePart(signer, "O");
        return LibrarySigners.Any(s => s.CommonName == commonName && s.Organization == organization);
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
