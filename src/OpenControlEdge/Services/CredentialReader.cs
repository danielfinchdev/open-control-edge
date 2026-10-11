using System.IO;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Reads Claude Code's credentials file (%USERPROFILE%\.claude\.credentials.json).
///
/// Only claudeAiOauth.accessToken, claudeAiOauth.expiresAt (unix milliseconds, verified) and
/// claudeAiOauth.subscriptionType (the plan: "free", "pro", "max"…; "pro" verified 2026-09-29) are decoded.
/// Every other value — refreshToken included — is skipped by the reader without ever being turned
/// into a string. The file is opened read-only with full sharing and is never written.
internal static class CredentialReader
{
    internal sealed record Credentials(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType);

    /// In CLAUDE_CONFIG_DIR when it names a usable folder (LoginFolder.FromEnvironment), as Claude Code does;
    /// %USERPROFILE%\.claude otherwise.
    public static string DefaultPath => Path.Combine(DefaultFolder, ".credentials.json");

    public static string DefaultFolder => DefaultFolderOnce.Value;

    private static readonly Lazy<string> DefaultFolderOnce = new(() => LoginFolder.FromEnvironment("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));

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
        DateTimeOffset? expiresAt = null;
        string? subscriptionType = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("claudeAiOauth"))
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
                if (reader.ValueTextEquals("accessToken"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String) accessToken = reader.GetString();
                }
                else if (reader.ValueTextEquals("expiresAt"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long ms)
                        && ms > 0 && ms < 253402300799999)
                    {
                        expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                    }
                }
                else if (reader.ValueTextEquals("subscriptionType"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String) subscriptionType = reader.GetString();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }

        return string.IsNullOrEmpty(accessToken) ? null : new Credentials(accessToken, expiresAt, subscriptionType);
    }

    /// Whether Claude Code still holds a sign-in it can use or refresh: claudeAiOauth.accessToken or
    /// claudeAiOauth.refreshToken is a non-empty string. Only the length of the raw values is looked at; neither is
    /// ever turned into a string. Verified 2026-09-30 on Claude Code 2.1.285: when its own refresh fails, the CLI
    /// rewrites both as "" and expiresAt as 0, and every later run answers "OAuth session expired".
    /// False when the file is missing or unreadable.
    public static bool HasSignIn(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            byte[]? buffer = LoginFolder.Read(path, 1024 * 1024, LoginFolder.UserProfile, out _);
            if (buffer is null) return false;
            try { return HasSignIn(buffer); }
            finally { Array.Clear(buffer); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal static bool HasSignIn(ReadOnlySpan<byte> json)
    {
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..];

        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("claudeAiOauth"))
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
                bool token = reader.ValueTextEquals("accessToken") || reader.ValueTextEquals("refreshToken");
                reader.Read();
                if (token && reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0) return true;
                reader.Skip();
            }
        }
        return false;
    }
}
