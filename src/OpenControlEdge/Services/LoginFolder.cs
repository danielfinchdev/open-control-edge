using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static OpenControlEdge.Interop.SecurityNative;

namespace OpenControlEdge.Services;

/// The login folders of the command-line clients as they find them themselves (CODEX_HOME, CLAUDE_CONFIG_DIR), and
/// how a login file found anywhere on the PC (AccountDiscovery) is opened by this process, which runs elevated.
///
/// The environment and every folder outside Windows' own are the plain user's to change, so an elevated read must not
/// become a way to read what the plain user cannot: a login file is only accepted when it is owned by this account (or
/// by Administrators — what a program of this account run as administrator creates — and only when the handle really
/// lies inside this account's profile), it is a plain file (not a link or reparse point, a single hard link) and of a
/// sane size. Every check is made on the open handle, so nothing can be swapped in between the check and the read.
internal static class LoginFolder
{
    private const string AdministratorsSid = "S-1-5-32-544";

    /// Cloud placeholders (OneDrive "files on demand"): opening the data would download them.
    private const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    private const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    private const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    private static readonly Lazy<string?> UserSid = new(() =>
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    });

    /// The folder an environment variable of the client points at (CODEX_HOME, CLAUDE_CONFIG_DIR), the way the client
    /// itself uses it; null when it is unset or not usable here: not a fully qualified path, missing, a link or reparse
    /// point, or not owned by this account.
    public static string? FromEnvironment(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable)?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 260 || !Path.IsPathFullyQualified(value)) return null;
        try
        {
            var info = new DirectoryInfo(value);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            string folder = Path.TrimEndingDirectorySeparator(info.FullName);
            var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return IsOwnedByUser(owner?.Value, folder, UserProfile) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// Whether path lies inside folder (or is it), comparing full paths without case.
    public static bool IsInside(string path, string folder)
    {
        if (folder.Length == 0) return false;
        string root = Path.TrimEndingDirectorySeparator(folder);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Length <= root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        // A drive's root ("C:\") keeps its separator.
        return root[^1] is '\\' or '/' || path[root.Length] is '\\' or '/';
    }

    private static bool IsOwnedByUser(string? owner, string finalPath, string profile) =>
        owner is not null && (owner == UserSid.Value || (owner == AdministratorsSid && IsInside(finalPath, profile)));

    /// The whole login file when it may be read (see the class): at most maxBytes, never a link, owned by this account;
    /// null otherwise or when unreadable. lastWriteUtc is its write time. The caller clears the buffer when done.
    public static byte[]? Read(string path, long maxBytes, string profile, out DateTime lastWriteUtc)
    {
        lastWriteUtc = default;
        using FileStream? stream = Open(path, maxBytes, profile, out long length, out lastWriteUtc);
        if (stream is null || length == 0) return null;
        var buffer = new byte[length];
        try
        {
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Array.Clear(buffer);
            return null;
        }
    }

    /// Whether the login file may be read (see the class), without reading it; lastWriteUtc is its write time.
    public static bool IsUsable(string path, long maxBytes, string profile, out DateTime lastWriteUtc)
    {
        using FileStream? stream = Open(path, maxBytes, profile, out _, out lastWriteUtc);
        return stream is not null;
    }

    private static FileStream? Open(string path, long maxBytes, string profile, out long length, out DateTime lastWriteUtc)
    {
        length = 0;
        lastWriteUtc = default;
        if (!Path.IsPathFullyQualified(path)) return null;
        // FILE_FLAG_OPEN_REPARSE_POINT: a link at this name is opened itself (and refused below), never followed.
        SafeFileHandle handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        FileStream? stream = null;
        bool keep = false;
        try
        {
            if (!Interop.ProcessNative.GetFileInformationByHandle(handle, out var info)) return null;
            const uint refused = FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_OFFLINE
                                 | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;
            // A hard link shares the file of another name: the plain user could link someone else's login here.
            if ((info.FileAttributes & refused) != 0 || info.NumberOfLinks != 1) return null;
            long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (size < 0 || size > maxBytes) return null;

            stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            var owner = stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            string? finalPath = owner?.Value == AdministratorsSid ? FinalPath(handle) : path;
            if (finalPath is null || !IsOwnedByUser(owner?.Value, finalPath, profile)) return null;

            length = size;
            lastWriteUtc = DateTime.FromFileTimeUtc(((long)(uint)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime);
            keep = true;
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                       or System.Security.SecurityException)
        {
            return null;
        }
        finally
        {
            // The stream owns the handle once it exists.
            if (!keep)
            {
                if (stream is not null) stream.Dispose();
                else handle.Dispose();
            }
        }
    }

    /// The path the handle really points at ("C:\Users\…", without the \\?\ prefix); null for a network path or on failure.
    private static string? FinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[1024];
        uint written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (written == 0 || written >= buffer.Length) return null;
        string path = new(buffer, 0, (int)written);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return null;
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : null;
    }
}
