using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static OpenControlEdge.Interop.SecurityNative;

namespace OpenControlEdge.Services;

/// %LOCALAPPDATA%\OpenControlEdge and the rules for writing into it from an elevated process.
///
/// The installer (Installer.HardenDataFolder, ported from tools\instalar.ps1) leaves it owned by Administrators, with a
/// protected DACL — SYSTEM and Administrators full control, Users read and execute — and a High mandatory label, after
/// refusing any link or reparse point inside it. Only then may the elevated widget write there: otherwise any program
/// running as the user could plant a link and redirect an elevated write elsewhere.
internal static class DataFolder
{
    /// Users, Everyone and Authenticated Users: groups without administrator rights.
    private static readonly string[] NonAdminSids = { "S-1-5-32-545", "S-1-1-0", "S-1-5-11" };

    /// Owner Administrators; protected DACL: SYSTEM and Administrators full control, Users read + execute; High label
    /// with no-write-up. The directory form carries inheritance to children.
    private const string DirectorySddl = "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)S:(ML;OICI;NW;;;HI)";
    private const string FileSddl = "O:BAD:PAI(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)S:(ML;;NW;;;HI)";

    private static bool _warnedUnsafe;

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenControlEdge");

    /// Not elevated: every write happens with the user's own rights, so any folder will do. Elevated: only a real
    /// folder (no link) that non-administrators cannot write to.
    public static bool IsSafeForWrites()
    {
        if (!UnelevatedLauncher.IsElevated) return true;
        try
        {
            var info = new DirectoryInfo(Path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && !NonAdminsCanWrite(Path, directory: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// Writes name inside the folder through a temporary file and a rename, so a crash never leaves half a file and an
    /// existing link at the final name is replaced, not followed. False (and one log line) when it is not safe.
    public static bool WriteAtomic(string name, byte[] bytes)
    {
        if (!IsSafeForWrites())
        {
            if (!_warnedUnsafe) Log.Warn("Data", $"no se escribe {name}: la carpeta de datos no está protegida (instala la aplicación)");
            _warnedUnsafe = true;
            return false;
        }
        try
        {
            if (!UnelevatedLauncher.IsElevated) Directory.CreateDirectory(Path);
            string final = System.IO.Path.Combine(Path, name);
            string temp = final + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, final, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Data", $"no se pudo escribir {name}: {ex.GetType().Name}");
            return false;
        }
    }

    /// Owners that are fine: Administrators, SYSTEM and TrustedInstaller. Any other owner can rewrite the DACL.
    private static readonly string[] AdminOwners =
        { "S-1-5-32-544", "S-1-5-18", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };

    /// True when Users, Everyone, Authenticated Users or this very account (whose unelevated programs are the ones to
    /// keep out) hold any write right on the file or directory, or own it (ported and widened from
    /// Test-EscrituraNoAdmin in instalar.ps1). Inherit-only entries count too: they reach every child.
    public static bool NonAdminsCanWrite(string path, bool directory)
    {
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || Array.IndexOf(AdminOwners, owner.Value) < 0) return true;

        using var self = WindowsIdentity.GetCurrent();
        string? selfSid = self.User?.Value;

        FileSystemRights writeMask = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
                                     | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete
                                     | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (directory) writeMask |= FileSystemRights.DeleteSubdirectoriesAndFiles;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            string sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (Array.IndexOf(NonAdminSids, sid) < 0 && sid != selfSid) continue;
            if ((rule.FileSystemRights & writeMask) != 0) return true;
        }
        return false;
    }

    /// Applies the protected security descriptor to the folder and everything inside it, each object through a
    /// handle opened without following reparse points. Throws InvalidOperationException (Spanish message) when a link
    /// or reparse point is found, or an object cannot be protected; the caller stops the installation then.
    public static void Harden()
    {
        Directory.CreateDirectory(Path);
        Protect(Path, directory: true);

        // The folder is protected now: nobody but an administrator can add or swap entries while it is walked.
        var pending = new Stack<string>();
        pending.Push(Path);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                Protect(entry, isDirectory);
                if (isDirectory) pending.Push(entry);
            }
        }
    }

    private static void Protect(string path, bool directory)
    {
        using SafeFileHandle handle = CreateFileW(path, READ_CONTROL | WRITE_DAC | WRITE_OWNER, FILE_SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new InvalidOperationException($"No se puede abrir {path} (error {Marshal.GetLastWin32Error()}).");

        if (!Interop.ProcessNative.GetFileInformationByHandle(handle, out var info))
            throw new InvalidOperationException($"No se puede comprobar {path}.");
        if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            throw new InvalidOperationException($"{path} es un enlace o punto de reanálisis; se cancela para evitar escrituras elevadas fuera de la carpeta de datos.");
        if (((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != directory)
            throw new InvalidOperationException($"{path} ha cambiado de tipo durante la comprobación.");
        // A hard link shares its security descriptor with the other name: protecting it would change a file elsewhere.
        if (!directory && info.NumberOfLinks > 1)
            throw new InvalidOperationException($"{path} tiene varios vínculos duros; se cancela.");

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(directory ? DirectorySddl : FileSddl, 1, out IntPtr descriptor, out _))
            throw new InvalidOperationException($"Descriptor de seguridad no válido (error {Marshal.GetLastWin32Error()}).");
        try
        {
            GetSecurityDescriptorOwner(descriptor, out IntPtr owner, out _);
            GetSecurityDescriptorDacl(descriptor, out _, out IntPtr dacl, out _);
            GetSecurityDescriptorSacl(descriptor, out _, out IntPtr sacl, out _);
            uint error = SetSecurityInfo(handle, SE_FILE_OBJECT,
                OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION,
                owner, IntPtr.Zero, dacl, sacl);
            if (error != 0) throw new InvalidOperationException($"No se pudo proteger {path} (error {error}).");
        }
        finally
        {
            LocalFree(descriptor);
        }
    }
}
