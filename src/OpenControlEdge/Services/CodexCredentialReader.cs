using System.Buffers.Text;
using System.IO;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Reads the Codex CLI login (%USERPROFILE%\.codex\auth.json).
///
/// Only tokens.access_token is decoded. Every other value — refresh_token and account_id included — is
/// skipped by the reader without ever being turned into a string. The file is opened read-only with full sharing
/// and is never written. The expiry comes from the access token itself (a JWT); only its "exp" claim is read.
/// The plan comes from tokens.id_token, decoded straight from the file bytes (never as a string); only its
/// "https://api.openai.com/auth".chatgpt_plan_type claim is read ("go" verified 2026-09-29).
internal static class CodexCredentialReader
{
    private const string AuthClaim = "https://api.openai.com/auth";

    internal sealed record Credentials(string AccessToken, DateTimeOffset? ExpiresAt, string? PlanType);

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
        string? planType = null;
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
                else if (reader.ValueTextEquals("id_token"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped) planType = JwtPlanType(reader.ValueSpan);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }

        return string.IsNullOrEmpty(accessToken) ? null : new Credentials(accessToken, JwtExpiry(accessToken), planType);
    }

    /// The chatgpt_plan_type claim of a JWT given as raw UTF-8 bytes; null when absent or not a JWT.
    /// Only that one claim is ever turned into a string; the decoded payload is cleared afterwards.
    internal static string? JwtPlanType(ReadOnlySpan<byte> token)
    {
        int first = token.IndexOf((byte)'.');
        if (first < 0) return null;
        ReadOnlySpan<byte> segment = token[(first + 1)..];
        int second = segment.IndexOf((byte)'.');
        if (second <= 0) return null;
        segment = segment[..second];

        // base64url → base64 with padding, into a buffer we own and can clear.
        byte[] base64 = new byte[(segment.Length + 3) / 4 * 4];
        byte[] payload = new byte[base64.Length / 4 * 3];
        try
        {
            for (int i = 0; i < base64.Length; i++)
                base64[i] = i >= segment.Length ? (byte)'=' : segment[i] switch { (byte)'-' => (byte)'+', (byte)'_' => (byte)'/', byte b => b };
            if (Base64.DecodeFromUtf8(base64, payload, out _, out int written) != System.Buffers.OperationStatus.Done) return null;

            var reader = new Utf8JsonReader(payload.AsSpan(0, written));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals(AuthClaim))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject) return null;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("chatgpt_plan_type"))
                    {
                        reader.Read();
                        return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    reader.Read();
                    reader.Skip();
                }
                return null;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            Array.Clear(base64);
            Array.Clear(payload);
        }
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
