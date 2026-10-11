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
/// "https://api.openai.com/auth" claims chatgpt_plan_type ("go" verified 2026-09-29), chatgpt_subscription_active_until
/// and chatgpt_subscription_last_checked (ISO 8601 or null, verified 2026-10-11) are read.
internal static class CodexCredentialReader
{
    private const string AuthClaim = "https://api.openai.com/auth";

    internal sealed record Credentials(string AccessToken, DateTimeOffset? ExpiresAt, string? PlanType)
    {
        /// Until when the subscription is paid, and when ChatGPT last checked it (the id_token is only renewed with
        /// the login, so the date may be in the past although the subscription renewed). Null when not in the token.
        public DateTimeOffset? SubscriptionUntil { get; init; }
        public DateTimeOffset? SubscriptionCheckedAt { get; init; }
    }

    /// The claims of the id_token that are read.
    internal readonly record struct AuthClaims(string? PlanType, DateTimeOffset? SubscriptionUntil, DateTimeOffset? SubscriptionCheckedAt);

    /// In CODEX_HOME when it names a usable folder (LoginFolder.FromEnvironment), as the Codex CLI does;
    /// %USERPROFILE%\.codex otherwise.
    public static string DefaultPath => Path.Combine(DefaultFolder, "auth.json");

    public static string DefaultFolder => DefaultFolderOnce.Value;

    private static readonly Lazy<string> DefaultFolderOnce = new(() => LoginFolder.FromEnvironment("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

    /// Read the checked way (LoginFolder.Read: not a link, a single hard link, owned by this account): this process runs
    /// elevated and the folder may be anywhere the plain user can write.
    public static Credentials? Read(string path)
    {
        if (!File.Exists(path)) return null;

        byte[]? buffer = LoginFolder.Read(path, 1024 * 1024, LoginFolder.UserProfile, out _);
        if (buffer is null) return null;

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
        AuthClaims claims = default;
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
                    if (reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped) claims = JwtAuthClaims(reader.ValueSpan);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }

        return string.IsNullOrEmpty(accessToken) ? null : new Credentials(accessToken, JwtExpiry(accessToken), claims.PlanType)
        {
            SubscriptionUntil = claims.SubscriptionUntil,
            SubscriptionCheckedAt = claims.SubscriptionCheckedAt,
        };
    }

    /// The chatgpt_plan_type claim of a JWT given as raw UTF-8 bytes; null when absent or not a JWT.
    internal static string? JwtPlanType(ReadOnlySpan<byte> token) => JwtAuthClaims(token).PlanType;

    /// The plan and subscription claims of a JWT given as raw UTF-8 bytes; empty when absent or not a JWT.
    /// Only those claims are ever turned into strings; the decoded payload is cleared afterwards.
    internal static AuthClaims JwtAuthClaims(ReadOnlySpan<byte> token)
    {
        int first = token.IndexOf((byte)'.');
        if (first < 0) return default;
        ReadOnlySpan<byte> segment = token[(first + 1)..];
        int second = segment.IndexOf((byte)'.');
        if (second <= 0) return default;
        segment = segment[..second];

        // base64url → base64 with padding, into a buffer we own and can clear.
        byte[] base64 = new byte[(segment.Length + 3) / 4 * 4];
        byte[] payload = new byte[base64.Length / 4 * 3];
        try
        {
            for (int i = 0; i < base64.Length; i++)
                base64[i] = i >= segment.Length ? (byte)'=' : segment[i] switch { (byte)'-' => (byte)'+', (byte)'_' => (byte)'/', byte b => b };
            if (Base64.DecodeFromUtf8(base64, payload, out _, out int written) != System.Buffers.OperationStatus.Done) return default;

            var reader = new Utf8JsonReader(payload.AsSpan(0, written));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return default;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals(AuthClaim))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject) return default;
                string? plan = null;
                DateTimeOffset? until = null, checkedAt = null;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("chatgpt_plan_type"))
                    {
                        reader.Read();
                        plan = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    else if (reader.ValueTextEquals("chatgpt_subscription_active_until"))
                    {
                        reader.Read();
                        until = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out DateTimeOffset date) ? date : null;
                    }
                    else if (reader.ValueTextEquals("chatgpt_subscription_last_checked"))
                    {
                        reader.Read();
                        checkedAt = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out DateTimeOffset date) ? date : null;
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }
                return new AuthClaims(plan, until, checkedAt);
            }
            return default;
        }
        catch (JsonException)
        {
            return default;
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
