using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Other logins of Claude Code, Codex and Cursor already on this PC, besides the default one of each:
/// - Claude Code: a CLAUDE_CONFIG_DIR folder holding .credentials.json with claudeAiOauth.accessToken;
/// - Codex: a CODEX_HOME folder holding auth.json with tokens.access_token;
/// - Cursor: a --user-data-dir folder whose User\globalStorage\state.vscdb holds a session (cursorAuth/accessToken).
///
/// Find is the quick look (the folders right inside the profile, %APPDATA% and %LOCALAPPDATA%: .codex-pro, .codex_2,
/// .claude-trabajo, Cursor-2…). ScanAsync / FindAllAsync also walk every ready fixed drive, a few folders deep, off the
/// UI thread and within a time and folder budget; links and reparse points are never followed, and system, program,
/// temporary, cache and dependency folders and other accounts' profiles are not entered.
///
/// Two folders of the same account are one, the most recently written: accounts are told apart by the SHA-256 of an id
/// in their files (Codex tokens.account_id, Claude oauthAccount.accountUuid in .claude.json, Cursor the token's user id,
/// hashed by the database reader), or of the access token when there is none. Only those hashes are kept, in memory;
/// no secret is ever turned into a string here, logged or stored, and every buffer is cleared. Files are opened through
/// LoginFolder (no links, a single hard link, a sane size, owned by this account). Nothing is ever written.
internal static class AccountDiscovery
{
    /// auth.json and .credentials.json, as their readers.
    private const long MaxLoginBytes = 1024 * 1024;
    /// .claude.json also keeps Claude Code's per-project history.
    private const long MaxClaudeConfigBytes = 32 * 1024 * 1024;
    /// As UntrustedSqlite.
    private const long MaxDatabaseBytes = 2L * 1024 * 1024 * 1024;

    /// How deep the walk goes below each root (D:\1\2\3\4\5\6), and how long and how many folders at most.
    internal const int MaxDepth = 6;
    internal static readonly TimeSpan WalkBudget = TimeSpan.FromMinutes(3);
    internal const int MaxFolders = 1_500_000;

    private const string CodexFile = "auth.json";
    private const string ClaudeFile = ".credentials.json";

    /// Folder names never entered, wherever they are: system, version control, dependencies and package caches.
    private static readonly FrozenSet<string> SkippedNames = new[]
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS",
        "Windows.old", "Config.Msi", "Recovery", "PerfLogs", "MSOCache", "WinSxS", "WindowsApps",
        ".git", ".svn", ".hg", "node_modules", "bower_components", "__pycache__", ".venv", "venv", "site-packages",
        ".tox", ".mypy_cache", ".pytest_cache", ".next", ".vs", "obj",
        ".nuget", ".npm", "npm-cache", ".pnpm-store", ".yarn", ".gradle", ".m2", ".cargo", ".rustup", ".cache",
        ".android", "Package Cache", "steamapps", "WindowsImageBackup", "OneDriveTemp",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// Not entered right below a drive's root: Windows' and programs' own folders.
    private static readonly FrozenSet<string> SkippedAtRoot = new[]
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SkippedNameLookup =
        SkippedNames.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SkippedAtRootLookup =
        SkippedAtRoot.GetAlternateLookup<ReadOnlySpan<char>>();

    /// Where to look and what each provider's default account is (Snapshot points it at fixture folders).
    /// Near: folders whose direct subfolders are looked at (the quick look). Roots: folders walked MaxDepth deep.
    /// Skipped: full paths never entered.
    internal sealed record Scope(string Profile, string ClaudeDefault, string CodexDefault, string CursorDefault,
        IReadOnlyList<string> Near, IReadOnlyList<string> Roots, IReadOnlyList<string> Skipped);

    /// What a search found: provider key → folders not yet in the settings, at most SettingsStore.AccountLimit
    /// counting the ones there; Complete is false when the walk ran out of time or folders.
    /// Roots: each root walked with its folders and time ("D:\ 12345 carpetas 3.2 s"), for the log.
    internal sealed record Report(IReadOnlyDictionary<string, IReadOnlyList<string>> Found, bool Complete, int Folders, TimeSpan Elapsed)
    {
        public IReadOnlyList<string> Roots { get; init; } = [];
    }

    private readonly record struct Candidate(string Provider, string Folder);

    private sealed record Account(string Folder, DateTime Written, string[] Ids);

    /// The quick look (no walk), for the settings page; null when the profile could not be looked through (then it is
    /// tried again another time). Cursor folders are only looked at when their name says Cursor: reading one costs a
    /// short helper process (LocalDatabaseReader).
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Find(Settings settings) => Find(settings, null);

    /// As Find, leaving out the folders the user dismissed.
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Find(Settings settings, IReadOnlySet<string>? dismissedFolders)
    {
        try
        {
            return RealScope(walk: false) is Scope scope ? Search(scope, settings, dismissedFolders, CancellationToken.None).Found : null;
        }
        catch (Exception ex)
        {
            // Never into the settings page: it simply shows no found folder.
            Log.Warn("Accounts", "detección de cuentas: " + ex.GetType().Name);
            return null;
        }
    }

    /// The quick look plus the walk of every fixed drive, on a background thread at low priority (it can take a few
    /// minutes on large disks). Null when cancelled or the profile could not be looked through.
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> FindAllAsync(Settings settings,
        IReadOnlySet<string>? dismissedFolders, CancellationToken ct) =>
        (await ScanAsync(settings, dismissedFolders, ct).ConfigureAwait(false))?.Found;

    /// FindAllAsync with how the walk went (whether it covered everything, how many folders, how long).
    public static Task<Report?> ScanAsync(Settings settings, IReadOnlySet<string>? dismissedFolders, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            // Background mode: low CPU and, above all, low disk priority while the drives are walked.
            bool background = Interop.ProcessNative.SetThreadPriority(Interop.ProcessNative.GetCurrentThread(),
                Interop.ProcessNative.THREAD_MODE_BACKGROUND_BEGIN);
            try
            {
                if (RealScope(walk: true) is not Scope scope) return null;
                Report report = Search(scope, settings, dismissedFolders, ct);
                Log.Info("Accounts", $"búsqueda de cuentas en los discos: {report.Folders} carpetas en {report.Elapsed.TotalSeconds:0.0} s, "
                                     + $"{report.Found.Values.Sum(f => f.Count)} cuentas nuevas" + (report.Complete ? "" : " (incompleta: límite de tiempo o carpetas)"));
                return report;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Accounts", ex);
                return null;
            }
            finally
            {
                if (background)
                    Interop.ProcessNative.SetThreadPriority(Interop.ProcessNative.GetCurrentThread(), Interop.ProcessNative.THREAD_MODE_BACKGROUND_END);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// This account's folders: the quick look in the profile, %APPDATA% and %LOCALAPPDATA%; with walk, the profile and
    /// then every ready fixed drive (the profile's parent, other accounts' profiles, is never entered).
    private static Scope? RealScope(bool walk)
    {
        string profile = LoginFolder.UserProfile;
        if (profile.Length == 0 || !Directory.Exists(profile)) return null;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var skipped = new List<string>
        {
            Path.GetTempPath(), Path.Combine(local, "Temp"), Path.Combine(local, "Packages"), Path.Combine(local, "Microsoft"),
            Path.Combine(appData, "Microsoft"), Path.Combine(profile, ".vscode", "extensions"), Path.Combine(profile, ".cursor", "extensions"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        var roots = new List<string>();
        if (walk)
        {
            roots.Add(profile);
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profile)) is { } users && Path.GetPathRoot(users) != users)
                skipped.Add(users);
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.Add(drive.RootDirectory.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        string cursorDefault = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(CursorCredentialReader.DefaultPath)!)!)!;
        return new Scope(profile, CredentialReader.DefaultFolder, CodexCredentialReader.DefaultFolder, cursorDefault,
            [profile, appData, local], roots, [.. skipped.Where(s => s.Length > 0)]);
    }

    /// The search itself; throws OperationCanceledException when ct is cancelled.
    internal static Report Search(Scope scope, Settings settings, IReadOnlySet<string>? dismissedFolders, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var skipped = new HashSet<string>(scope.Skipped.Select(Normalize).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string provider, string folder)
        {
            if (seen.Add(provider + "|" + folder)) candidates.Add(new Candidate(provider, folder));
        }

        foreach (string near in scope.Near)
        {
            IEnumerable<string> folders;
            try { folders = [.. Directory.EnumerateDirectories(near)]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (string folder in folders)
            {
                if (skipped.Contains(folder) || IsLink(folder)) continue;
                if (File.Exists(Path.Combine(folder, CodexFile))) Add(AiProviderSettings.Codex, folder);
                if (File.Exists(Path.Combine(folder, ClaudeFile))) Add(AiProviderSettings.Claude, folder);
                if (Path.GetFileName(folder).Contains("cursor", StringComparison.OrdinalIgnoreCase) && CursorDatabase(folder) is not null)
                    Add(AiProviderSettings.Cursor, folder);
            }
        }

        bool complete = true;
        int walked = 0;
        var walkedRoots = new List<string>();
        var rootStats = new List<string>();
        foreach (string root in scope.Roots)
        {
            ct.ThrowIfCancellationRequested();
            if (Normalize(root) is not string start || walkedRoots.Any(r => LoginFolder.IsInside(start, r))) continue;
            // A root walked before (the profile) is not walked again inside the next one (its drive).
            var skipHere = new HashSet<string>(skipped, StringComparer.OrdinalIgnoreCase);
            skipHere.UnionWith(walkedRoots);
            int before = walked;
            TimeSpan started = clock.Elapsed;
            complete &= Walk(start, skipHere, Add, clock, ref walked, ct);
            walkedRoots.Add(start);
            rootStats.Add($"{start} {walked - before} carpetas {(clock.Elapsed - started).TotalSeconds:0.0} s");
            if (!complete) break;
        }

        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string provider in new[] { AiProviderSettings.Claude, AiProviderSettings.Codex, AiProviderSettings.Cursor })
        {
            ct.ThrowIfCancellationRequested();
            List<string> folders = Choose(provider, scope, settings, dismissedFolders, candidates.Where(c => c.Provider == provider).Select(c => c.Folder), ct);
            if (folders.Count > 0) found[provider] = folders;
        }
        return new Report(found, complete, walked, clock.Elapsed) { Roots = rootStats };
    }

    /// One root, depth first, MaxDepth deep; false when the budget ran out. Every folder that cannot be listed is
    /// passed over; links, reparse points and cloud placeholders are never entered.
    private static bool Walk(string root, HashSet<string> skipped, Action<string, string> add, Stopwatch clock, ref int walked, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
        };
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out (string Path, int Depth) item))
        {
            ct.ThrowIfCancellationRequested();
            if (walked >= MaxFolders || clock.Elapsed > WalkBudget) return false;
            walked++;
            bool hasUser = false;
            bool atRoot = Path.GetPathRoot(item.Path) is string driveRoot && driveRoot.Length == item.Path.Length;
            try
            {
                var entries = new FileSystemEnumerable<(string Path, string Name, bool Directory)>(item.Path,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.FileName.ToString(), entry.IsDirectory), options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => Include(ref entry, atRoot),
                };
                foreach ((string path, string name, bool directory) in entries)
                {
                    if (!directory)
                    {
                        add(name.Equals(CodexFile, StringComparison.OrdinalIgnoreCase) ? AiProviderSettings.Codex : AiProviderSettings.Claude, item.Path);
                        continue;
                    }
                    if (name.Equals("User", StringComparison.OrdinalIgnoreCase)) hasUser = true;
                    if (item.Depth < MaxDepth && !skipped.Contains(path)) pending.Push((path, item.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                continue;
            }
            // Only profiles whose path says Cursor: VS Code, VSCodium, Windsurf… have the same database.
            if (hasUser && item.Path.Contains("cursor", StringComparison.OrdinalIgnoreCase) && CursorDatabase(item.Path) is not null)
                add(AiProviderSettings.Cursor, item.Path);
        }
        return true;
    }

    /// Real folders worth entering, and files named like a login; nothing that is a link, reparse point or placeholder.
    private static bool Include(ref FileSystemEntry entry, bool atRoot)
    {
        const FileAttributes placeholder = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
        if ((entry.Attributes & placeholder) != 0) return false;
        if (!entry.IsDirectory)
            return entry.FileName.Equals(CodexFile, StringComparison.OrdinalIgnoreCase) || entry.FileName.Equals(ClaudeFile, StringComparison.OrdinalIgnoreCase);
        if (SkippedNameLookup.Contains(entry.FileName)) return false;
        return !atRoot || !SkippedAtRootLookup.Contains(entry.FileName);
    }

    /// folder\User\globalStorage\state.vscdb when it is there and no folder on the way is a link; null otherwise. The
    /// database itself is opened by LoginFolder and parsed as the plain user (LocalDatabaseReader).
    private static string? CursorDatabase(string folder)
    {
        string user = Path.Combine(folder, "User"), storage = Path.Combine(user, "globalStorage"), database = Path.Combine(storage, "state.vscdb");
        try
        {
            return IsRealFolder(user) && IsRealFolder(storage) && File.Exists(database) ? database : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsRealFolder(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool IsLink(string folder)
    {
        try { return (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// The candidates of one provider that are real logins of accounts not shown yet, freshest first, within the limit.
    private static List<string> Choose(string provider, Scope scope, Settings settings, IReadOnlySet<string>? dismissedFolders,
        IEnumerable<string> folders, CancellationToken ct)
    {
        var result = new List<string>();
        string[] configured = [.. settings.AccountsOf(provider).Select(a => a.Folder)];
        int room = SettingsStore.AccountLimit - configured.Length;
        if (room <= 0) return result;

        string defaultFolder = provider switch
        {
            AiProviderSettings.Claude => scope.ClaudeDefault,
            AiProviderSettings.Codex => scope.CodexDefault,
            _ => scope.CursorDefault,
        };
        IEnumerable<string> dismissed = dismissedFolders ?? (IEnumerable<string>)Array.Empty<string>();
        var knownFolders = new List<string> { defaultFolder };
        knownFolders.AddRange(configured);
        knownFolders.AddRange(dismissed);
        var known = new HashSet<string>(knownFolders.Select(Normalize).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        List<string> fresh = [.. folders.Where(f => Normalize(f) is string n && !known.Contains(n))];
        if (fresh.Count == 0) return result;

        // The accounts already shown (and the dismissed ones): the same account in another folder is not offered again.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in known)
        {
            ct.ThrowIfCancellationRequested();
            if (Identify(provider, folder, scope) is Account account) ids.UnionWith(account.Ids);
        }

        var accounts = new List<Account>();
        foreach (string folder in fresh)
        {
            ct.ThrowIfCancellationRequested();
            if (Identify(provider, folder, scope) is Account account) accounts.Add(account);
        }
        // The freshest login first: of two folders of the same account, the one in use is kept.
        foreach (Account account in accounts.OrderByDescending(a => a.Written))
        {
            if (result.Count >= room) break;
            if (account.Ids.Any(ids.Contains)) continue;
            ids.UnionWith(account.Ids);
            result.Add(account.Folder);
        }
        return result;
    }

    /// A login file nobody has written for this long is a copy or a backup (an active client rewrites it when its token is
    /// renewed): not added.
    private static readonly TimeSpan StaleLogin = TimeSpan.FromDays(60);

    /// The account a folder is a real login of (its ids and when its login was written); null when it is not one, or a
    /// stale one.
    private static Account? Identify(string provider, string folder, Scope scope)
    {
        Account? account = IdentifyAny(provider, folder, scope);
        return account is not null && DateTime.UtcNow - account.Written > StaleLogin ? null : account;
    }

    private static Account? IdentifyAny(string provider, string folder, Scope scope)
    {
        switch (provider)
        {
            case AiProviderSettings.Codex:
            {
                byte[]? json = LoginFolder.Read(Path.Combine(folder, CodexFile), MaxLoginBytes, scope.Profile, out DateTime written);
                if (json is null) return null;
                try
                {
                    // The same condition as CodexCredentialReader.Parse: a non-empty tokens.access_token.
                    if (StringHash(json, "tokens", "access_token") is not string token) return null;
                    return new Account(folder, written, [StringHash(json, "tokens", "account_id") is string id ? "id:" + id : "token:" + token]);
                }
                finally { Array.Clear(json); }
            }
            case AiProviderSettings.Claude:
            {
                byte[]? json = LoginFolder.Read(Path.Combine(folder, ClaudeFile), MaxLoginBytes, scope.Profile, out DateTime written);
                if (json is null) return null;
                string token;
                try
                {
                    // The same condition as CredentialReader.Parse: a non-empty claudeAiOauth.accessToken.
                    if (StringHash(json, "claudeAiOauth", "accessToken") is not string hash) return null;
                    token = hash;
                }
                finally { Array.Clear(json); }
                return ClaudeAccountUuid(folder, scope) is string uuid
                    ? new Account(folder, written, ["uuid:" + uuid, "token:" + token])
                    : new Account(folder, written, ["token:" + token]);
            }
            default:
            {
                if (CursorDatabase(folder) is not string database
                    || !LoginFolder.IsUsable(database, MaxDatabaseBytes, scope.Profile, out DateTime written)) return null;
                try
                {
                    return LocalDatabaseReader.ReadCursorAccount(database) is string id ? new Account(folder, written, ["id:" + id]) : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    return null;
                }
            }
        }
    }

    /// SHA-256 of oauthAccount.accountUuid in the folder's .claude.json, where Claude Code keeps the signed-in account:
    /// inside CLAUDE_CONFIG_DIR, or %USERPROFILE%\.claude.json for the profile's own .claude folder.
    private static string? ClaudeAccountUuid(string folder, Scope scope)
    {
        string own = Path.Combine(scope.Profile, ".claude");
        string config = Normalize(folder) is string f && Normalize(own) is string o && f.Equals(o, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(scope.Profile, ".claude.json")
            : Path.Combine(folder, ".claude.json");
        byte[]? json = LoginFolder.Read(config, MaxClaudeConfigBytes, scope.Profile, out _);
        if (json is null) return null;
        try { return StringHash(json, "oauthAccount", "accountUuid"); }
        finally { Array.Clear(json); }
    }

    /// SHA-256 (hex) of the raw bytes of the non-empty string outer.inner of a JSON object; null when there is none or
    /// the JSON is not valid. The value is never turned into a string.
    internal static string? StringHash(ReadOnlySpan<byte> json, string outer, string inner)
    {
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..];
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            string? hash = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool match = reader.ValueTextEquals(outer);
                reader.Read();
                if (!match || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool found = reader.ValueTextEquals(inner);
                    reader.Read();
                    if (found && hash is null && reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0)
                        hash = Convert.ToHexString(SHA256.HashData(reader.ValueSpan));
                    reader.Skip();
                }
            }
            // The whole file is read: a truncated or broken one is not a login, as for the readers themselves.
            return reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0 && !reader.Read() ? hash : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// The full path without a trailing separator; null when it is not a valid path.
    private static string? Normalize(string folder)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
