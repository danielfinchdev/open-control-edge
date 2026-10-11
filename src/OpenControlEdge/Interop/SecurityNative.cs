using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenControlEdge.Interop;

/// advapi32 / kernel32: security descriptors applied through an open handle (never through a path, so a link swapped
/// in after the check cannot redirect them).
internal static class SecurityNative
{
    public const uint READ_CONTROL = 0x00020000;
    public const uint WRITE_DAC = 0x00040000;
    public const uint WRITE_OWNER = 0x00080000;
    public const uint FILE_SHARE_ALL = 0x7;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    public const int SE_FILE_OBJECT = 1;
    public const uint OWNER_SECURITY_INFORMATION = 0x1;
    public const uint DACL_SECURITY_INFORMATION = 0x4;
    public const uint LABEL_SECURITY_INFORMATION = 0x10;
    public const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
    public const uint UNPROTECTED_DACL_SECURITY_INFORMATION = 0x20000000;

    // A plain-user copy of this process's token (OrbBridge): no privileges, Administrators deny-only, Medium label.
    public const uint DISABLE_MAX_PRIVILEGE = 0x1;
    public const int TokenOwner = 4;
    public const uint SE_GROUP_INTEGRITY = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableSidCount,
        SID_AND_ATTRIBUTES[]? sidsToDisable, uint deletePrivilegeCount, IntPtr privilegesToDelete, uint restrictedSidCount,
        IntPtr sidsToRestrict, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSidToSidW(string sid, out IntPtr result);

    [DllImport("advapi32.dll")]
    public static extern int GetLengthSid(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetSecurityDescriptorOwner(IntPtr descriptor, out IntPtr owner, out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);

    [DllImport("advapi32.dll")]
    public static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, IntPtr owner,
        IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);

    public const int ERROR_ALREADY_EXISTS = 183;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateBoundaryDescriptorW(string name, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AddSIDToBoundaryDescriptor(ref IntPtr boundary, IntPtr sid);

    [DllImport("kernel32.dll")]
    public static extern void DeleteBoundaryDescriptor(IntPtr boundary);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreatePrivateNamespaceW(IntPtr attributes, IntPtr boundary, string alias);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenPrivateNamespaceW(IntPtr boundary, string alias);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ClosePrivateNamespace(IntPtr handle, uint flags);
}
