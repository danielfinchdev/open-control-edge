using System.IO;
using System.Security.Cryptography;

namespace OpenControlEdge.Services;

/// Stores one DPAPI CurrentUser protected blob per network-backed provider (keys\{provider}.bin in the data folder).
/// DPAPI ties the blob to this Windows account: another account cannot decrypt it, but any program running as this
/// user can, as with any per-user secret on Windows.
internal static class ProviderKeyStore
{
    /// The provider refused the stored key (HTTP 401).
    public const string InvalidKeyMessage = "Clave API no válida";

    private static string KeyDirectory => Path.Combine(DataFolder.Path, "keys");

    internal static string PathFor(string provider) => Path.Combine(KeyDirectory, provider + ".bin");

    internal static bool IsConfigured(string provider) => File.Exists(PathFor(provider));

    internal static bool Save(string provider, byte[] plaintext)
    {
        byte[] protectedBytes = Array.Empty<byte>();
        try
        {
            if (plaintext.Length == 0 || plaintext.Length > 16 * 1024) return false;
            protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            return DataFolder.WriteAtomic(Path.Combine("keys", provider + ".bin"), protectedBytes);
        }
        catch (CryptographicException) { return false; }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
    }

    internal static byte[]? Read(string provider)
    {
        byte[]? protectedBytes = null;
        try
        {
            string path = PathFor(provider);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 32 * 1024) return null;
            protectedBytes = File.ReadAllBytes(path);
            return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
        finally { if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes); }
    }

    internal static bool Delete(string provider)
    {
        try
        {
            if (!DataFolder.IsSafeForWrites()) return false;
            string path = PathFor(provider);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
