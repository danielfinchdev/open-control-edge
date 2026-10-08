using System.IO;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Reads Cursor's IDE session from state.vscdb (LocalDatabaseReader: parsed without administrator rights, read-only,
/// never modified). Only two keys are read:
/// cursorAuth/accessToken and cursorAuth/stripeMembershipType. The user id is the "sub" claim of the token (a JWT),
/// after the provider prefix ("auth0|user_…" → "user_…"); nothing else of the token is decoded.
internal static class CursorCredentialReader
{
    public sealed record Credentials(string UserId, string AccessToken, string? MembershipType);

    public static string DefaultPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public static Credentials? Read(string path)
    {
        if (!File.Exists(path)) return null;
        if (LocalDatabaseReader.ReadCursor(path) is not LocalDatabaseReader.CursorSession session) return null;
        string token = session.Token;
        string? membership = session.Membership;

        string? subject = JwtSubject(token);
        if (string.IsNullOrWhiteSpace(subject)) return null;
        int separator = subject.IndexOf('|');
        string userId = separator >= 0 ? subject[(separator + 1)..] : subject;
        return string.IsNullOrWhiteSpace(userId) ? null : new Credentials(userId, token, membership);
    }

    /// The "sub" claim of a JWT; null when the token is not a JWT or has none. The decoded payload is cleared.
    private static string? JwtSubject(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length < 2) return null;
        string base64 = parts[1].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
        byte[] payload;
        try { payload = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }

        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool subjectClaim = reader.ValueTextEquals("sub");
                reader.Read();
                if (subjectClaim) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }
}
