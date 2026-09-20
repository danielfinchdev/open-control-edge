using System.IO;
using System.Text.Json;

namespace EdgeWidget.Services;

/// Reads Claude Code's credentials file (%USERPROFILE%\.claude\.credentials.json).
///
/// Only claudeAiOauth.accessToken and claudeAiOauth.expiresAt (unix milliseconds, verified) are decoded.
/// Every other value — refreshToken included — is skipped by the reader without ever being turned
/// into a string. The file is opened read-only with full sharing and is never written.
internal static class CredentialReader
{
    internal sealed record Credentials(string AccessToken, DateTimeOffset? ExpiresAt);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

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
        DateTimeOffset? expiresAt = null;

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
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }

        return string.IsNullOrEmpty(accessToken) ? null : new Credentials(accessToken, expiresAt);
    }
}
