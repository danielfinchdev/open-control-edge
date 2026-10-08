using System.Security.Cryptography;

namespace OpenControlEdge.Services;

/// The project's own signature of each release archive: the Release workflow signs OpenControlEdge-win-x64.zip with an
/// ECDSA P-256 key kept as a secret of the repository (UPDATE_SIGNING_KEY) and publishes the signature next to it as
/// OpenControlEdge-win-x64.zip.sig (IEEE P1363, base64). The updater installs a signed archive only when the signature
/// matches this public key, so a file swapped on the way or in the Release itself is refused even without Authenticode.
internal static class UpdateSignature
{
    internal const string InvalidMessage = "La firma de la actualización no es válida; no se instalará.";

    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcnoBYyNduPPypM6tjUZ/6H+iknB3
        s+Pr2rukRZGgnypW0Ekf5ZRnZ4VwvlfI2PLS1zEmzUJd277o28WnIixt8g==
        -----END PUBLIC KEY-----
        """;

    /// signature: the .sig file as downloaded (base64 text of the 64-byte signature).
    internal static bool Verify(byte[] archive, byte[] signature)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signature).Trim());
            if (raw.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportFromPem(PublicKeyPem);
            return key.VerifyData(archive, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
