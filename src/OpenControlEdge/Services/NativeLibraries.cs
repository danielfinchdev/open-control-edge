using System.IO;
using System.Security.Cryptography;

namespace OpenControlEdge.Services;

/// The native libraries published next to OpenControlEdge.exe. Sha256 (file name → lowercase hex) is generated at build
/// time from the very files this build publishes (OceNativeLibraryHashes in the csproj), so the exe carries the exact
/// bytes of its own companions. Empty in Debug, which is not self-contained.
internal static partial class NativeLibraries
{
    public static IEnumerable<string> Names => Sha256.Keys;

    /// The file is the one this build published under that name.
    public static bool IsExpected(string path)
    {
        if (!Sha256.TryGetValue(Path.GetFileName(path), out string? expected)) return false;
        return string.Equals(HashHex(path), expected, StringComparison.OrdinalIgnoreCase);
    }

    public static string HashHex(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
