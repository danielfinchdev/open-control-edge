using System.IO;
using System.Text.Json;

namespace EdgeWidget.Services;

/// Reads the Codex CLI login (%USERPROFILE%\.codex\auth.json).
///
/// Only tokens.access_token is decoded. Every other value — refresh_token, id_token and account_id included — is
/// skipped by the reader without ever being turned into a string. The file is opened read-only with full sharing
/// and is never written. The expiry comes from the access token itself (a JWT); only its "exp" claim is read.
internal static class CodexCredentialReader
{
    internal sealed record Credentials(string AccessToken, DateTimeOffset? ExpiresAt);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    public static Credentials? Read(string path)
    {
        if (!File.Exists(path)) return null;

        byte[] buffer;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length is 0 or > 1024 * 1024) return null;
            buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
        }

        try
        {
            return Parse(buffer);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    internal static Credentials? Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..];

        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

        string? accessToken = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("tokens"))
            {
                reader.Read();
                reader.Skip();
                continue;
            }

            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("access_token"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String) accessToken = reader.GetString();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }

        return string.IsNullOrEmpty(accessToken) ? null : new Credentials(accessToken, JwtExpiry(accessToken));
    }

    /// The "exp" claim (unix seconds) of a JWT payload; null when the token is not a JWT or carries no exp.
    internal static DateTimeOffset? JwtExpiry(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3) return null;

        byte[] payload;
        try
        {
            string base64 = parts[1].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            payload = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("exp"))
                {
                    reader.Read();
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long seconds)
                           && seconds > 0 && seconds < 253402300799
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                        : null;
                }
                reader.Read();
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
            Array.Clear(payload);
        }
    }
}
