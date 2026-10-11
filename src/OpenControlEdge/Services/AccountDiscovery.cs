using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Other logins of Claude Code and Codex already on this PC: configuration folders next to the default one in the user
/// profile (%USERPROFILE%\.codex-pro, .codex_2, .claude-trabajo…), the ones CLAUDE_CONFIG_DIR / CODEX_HOME point at.
///
/// Only the presence of the login file is checked, plus, for Codex, tokens.account_id to tell accounts apart (two
/// folders of the same account are one). That id is only hashed in memory: never turned into a string, never logged,
/// never stored. Nothing is ever written.
internal static class AccountDiscovery
{
    private const long MaxAuthBytes = 1024 * 1024;

    /// The folders not yet in the settings, at most SettingsStore.AccountLimit per provider (provider key → folders);
    /// null when the profile could not be looked through (then it is tried again another time).
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Find(Settings settings)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length == 0 || !Directory.Exists(profile)) return found;
            Add(found, AiProviderSettings.Codex, Codex(profile, settings));
            Add(found, AiProviderSettings.Claude, Claude(profile, settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Accounts", "detección de cuentas: " + ex.GetType().Name);
            return null;
        }
        return found;
    }

    private static void Add(Dictionary<string, IReadOnlyList<string>> map, string provider, List<string> folders)
    {
        if (folders.Count > 0) map[provider] = folders;
    }

    /// .codex* folders with an auth.json of an account other than the default one's (and other than each other).
    private static List<string> Codex(string profile, Settings settings)
    {
        string defaultFolder = Path.GetDirectoryName(CodexCredentialReader.DefaultPath)!;
        var known = new HashSet<string>(settings.AccountsOf(AiProviderSettings.Codex).Select(a => Normalize(a.Folder)), StringComparer.OrdinalIgnoreCase)
            { Normalize(defaultFolder) };
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExtraAccount account in settings.AccountsOf(AiProviderSettings.Codex))
            if (AccountHash(Path.Combine(account.Folder, "auth.json")) is string id) accounts.Add(id);
        if (AccountHash(CodexCredentialReader.DefaultPath) is string main) accounts.Add(main);

        var result = new List<string>();
        int room = SettingsStore.AccountLimit - settings.AccountsOf(AiProviderSettings.Codex).Length;
        // The freshest login first: of two folders of the same account, the one in use is kept.
        foreach (string folder in Candidates(profile, ".codex", "auth.json")
                     .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(f, "auth.json"))))
        {
            if (result.Count >= room) break;
            if (known.Contains(Normalize(folder))) continue;
            string? id = AccountHash(Path.Combine(folder, "auth.json"));
            if (id is null || !accounts.Add(id)) continue;
            result.Add(folder);
        }
        return result;
    }

    /// .claude* folders with a .credentials.json (Claude Code's own one is %USERPROFILE%\.claude).
    private static List<string> Claude(string profile, Settings settings)
    {
        string defaultFolder = Path.GetDirectoryName(CredentialReader.DefaultPath)!;
        var known = new HashSet<string>(settings.AccountsOf(AiProviderSettings.Claude).Select(a => Normalize(a.Folder)), StringComparer.OrdinalIgnoreCase)
            { Normalize(defaultFolder) };
        int room = SettingsStore.AccountLimit - settings.AccountsOf(AiProviderSettings.Claude).Length;
        return [.. Candidates(profile, ".claude", ".credentials.json").Where(f => !known.Contains(Normalize(f))).Take(Math.Max(0, room))];
    }

    /// Real folders (no links) of the profile whose name is the prefix alone or the prefix and a separator
    /// (".codex-pro", ".codex_2", ".codex.trabajo"), holding the login file.
    private static IEnumerable<string> Candidates(string profile, string prefix, string loginFile)
    {
        foreach (string folder in Directory.EnumerateDirectories(profile, prefix + "*"))
        {
            string name = Path.GetFileName(folder);
            if (name.Length > prefix.Length && name[prefix.Length] is not ('-' or '_' or '.')) continue;
            if (name.Length > 60) continue;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            string login = Path.Combine(folder, loginFile);
            bool usable;
            try { usable = File.Exists(login) && (File.GetAttributes(login) & FileAttributes.ReparsePoint) == 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { usable = false; }
            if (usable) yield return folder;
        }
    }

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// SHA-256 of tokens.account_id in a Codex auth.json, as hex; null when unreadable or absent. The raw bytes of the
    /// file and of the id are cleared afterwards.
    internal static string? AccountHash(string authPath)
    {
        byte[]? buffer = null;
        try
        {
            if (!File.Exists(authPath)) return null;
            using (var stream = new FileStream(authPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length is 0 or > MaxAuthBytes) return null;
                buffer = new byte[stream.Length];
                stream.ReadExactly(buffer);
            }
            ReadOnlySpan<byte> json = buffer;
            if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..];
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool tokens = reader.ValueTextEquals("tokens");
                reader.Read();
                if (!tokens || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool id = reader.ValueTextEquals("account_id");
                    reader.Read();
                    if (id && reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped && reader.ValueSpan.Length > 0)
                        return Convert.ToHexString(SHA256.HashData(reader.ValueSpan));
                    reader.Skip();
                }
                return null;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        finally
        {
            if (buffer is not null) Array.Clear(buffer);
        }
    }
}
