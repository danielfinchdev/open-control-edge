using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Reads Cursor's IDE session from state.vscdb without modifying the database.
internal static class CursorCredentialReader
{
    public sealed record Credentials(string UserId, string AccessToken, string? MembershipType);

    public static string DefaultPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public static Credentials? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private };
        using var connection = new SqliteConnection(builder.ToString() + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM ItemTable WHERE key IN ('cursorAuth/accessToken', 'cursorAuth/stripeMembershipType')";
        string? token = null, membership = null;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (reader.GetString(0) == "cursorAuth/accessToken") token = reader.IsDBNull(1) ? null : reader.GetString(1);
                else membership = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length < 2) return null;
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            byte[] bytes = Convert.FromBase64String(payload);
            string? subject;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                subject = document.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String ? sub.GetString() : null;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            if (string.IsNullOrWhiteSpace(subject)) return null;
            int separator = subject.IndexOf('|');
            string userId = separator >= 0 ? subject[(separator + 1)..] : subject;
            return string.IsNullOrWhiteSpace(userId) ? null : new Credentials(userId, token, membership);
        }
        finally { token = null; }
    }
}
