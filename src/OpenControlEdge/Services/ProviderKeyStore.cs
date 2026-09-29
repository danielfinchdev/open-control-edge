using System.IO;
using System.Security.Cryptography;

namespace OpenControlEdge.Services;

/// Stores one DPAPI CurrentUser protected blob per network-backed provider.
internal static class ProviderKeyStore
{
    private static string KeyDirectory => Path.Combine(DataFolder.Path, "keys");

    internal static string PathFor(string provider) => Path.Combine(KeyDirectory, provider + ".bin");

    internal static bool IsConfigured(string provider) => File.Exists(PathFor(provider));

    internal static bool Save(string provider, byte[] plaintext)
    {
        byte[] protectedBytes = Array.Empty<byte>();
        string temporary = PathFor(provider) + ".tmp";
        try
        {
            if (plaintext.Length == 0 || plaintext.Length > 16 * 1024) return false;
            protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            return DataFolder.WriteAtomic(Path.Combine("keys", provider + ".bin"), protectedBytes);
        }
        catch { return false; }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
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
        catch { return null; }
        finally { if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes); }
    }

    internal static bool Delete(string provider)
    {
        try
        {
            string path = PathFor(provider);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}
