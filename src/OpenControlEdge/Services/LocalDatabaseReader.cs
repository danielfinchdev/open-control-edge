using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OpenControlEdge.Services;

/// Reads the two SQLite databases of other programs — Cursor's state.vscdb and OpenCode's opencode.db — without this
/// process's administrator rights. Any program of the user can write those files, so the installed (elevated) copy
/// never parses them itself: it starts its own executable as the plain user (UnelevatedLauncher, with
/// __COMPAT_LAYER=RunAsInvoker so the requireAdministrator manifest does not ask for elevation), hidden and inside a
/// kill-on-close job, with HelperArgument. That short-lived copy opens the file with UntrustedSqlite, writes one small
/// JSON object to stdout and exits; this side only reads that answer, strictly and size-capped. A copy that already runs
/// unelevated (Debug) reads in-process: there is no privilege to protect.
///
/// It costs one process start (~0.1 s of CPU, ~30 MB for under a second) when a database changed since the last read
/// (its size or time, or its -wal file's), and nothing otherwise: the previous answer is reused. The elevated process
/// no longer loads the native SQLite library at all.
internal static class LocalDatabaseReader
{
    internal const string HelperArgument = "--read-database";

    /// The reader could not be started as the plain user (no desktop, UAC off, another account on the desktop…).
    public const string UnavailableMessage = "No se pudo leer sin permisos de administrador";
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(20);
    private const int MaxTokenLength = 8 * 1024;

    internal sealed record CursorSession(string Token, string? Membership);

    internal sealed record OpenCodeTotals(long Input, long Output, long Reasoning, long CacheRead, long CacheWrite, decimal Cost);

    internal enum Failure { Missing, Busy, Incompatible, Invalid, Unavailable }

    /// Why a database could not be read; Detail is a short reason for the log (never a secret).
    internal sealed class ReadException(Failure kind, string detail) : IOException(detail)
    {
        public Failure Kind { get; } = kind;
    }

    /// The last answer for each database, with the file stamps it was read at.
    private static readonly Dictionary<string, (string Stamp, object? Answer)> Answers = new(StringComparer.OrdinalIgnoreCase);

    /// Size and write time of the files (and their -wal journals); null when one of them cannot be read.
    private static string? Stamp(IEnumerable<string> paths)
    {
        try
        {
            var stamp = new System.Text.StringBuilder();
            foreach (string path in paths)
            {
                var file = new FileInfo(path);
                var wal = new FileInfo(path + "-wal");
                if (!file.Exists) return null;
                stamp.Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks).Append(':')
                    .Append(wal.Exists ? $"{wal.Length}:{wal.LastWriteTimeUtc.Ticks}" : "-").Append('|');
            }
            return stamp.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The answer read the last time if none of the files changed since; otherwise read and remembered.
    private static T Cached<T>(string key, IEnumerable<string> paths, Func<T> read)
    {
        string? stamp = Stamp(paths);
        lock (Answers)
            if (stamp is not null && Answers.TryGetValue(key, out var known) && known.Stamp == stamp) return (T)known.Answer!;
        T answer = read();
        if (stamp is not null) lock (Answers) Answers[key] = (stamp, answer);
        return answer;
    }

    /// Cursor's session token and membership; null when Cursor holds no session.
    public static CursorSession? ReadCursor(string path)
    {
        if (!UnelevatedLauncher.IsElevated) return CursorInProcess(path);
        return Cached("cursor|" + path, [path], () => CursorFromHelper(path));
    }

    private static CursorSession? CursorFromHelper(string path)
    {
        using JsonDocument answer = AskHelper("cursor", path);
        JsonElement root = answer.RootElement;
        if (!root.TryGetProperty("token", out JsonElement token)) return null;
        string? value = token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTokenLength || value.Any(char.IsControl))
            throw new ReadException(Failure.Invalid, "respuesta del lector con un token no válido");
        string? membership = root.TryGetProperty("membership", out JsonElement m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() : null;
        if (membership is not null && (membership.Length > 64 || membership.Any(char.IsControl))) membership = null;
        return new CursorSession(value, membership);
    }

    /// OpenCode's session totals; path null finds the database the way OpenCode does (OPENCODE_DB, XDG_DATA_HOME…).
    public static OpenCodeTotals ReadOpenCode(string? path)
    {
        if (!UnelevatedLauncher.IsElevated) return OpenCodeInProcess(path);
        // Only the files' metadata is looked at here, to know whether anything changed; the helper finds and reads them.
        string[] files = path is not null and not "auto" ? [path] : OpenCodeDatabasePaths();
        return Cached("opencode|" + (path ?? "auto"), files, () => OpenCodeFromHelper(path));
    }

    private static OpenCodeTotals OpenCodeFromHelper(string? path)
    {
        using JsonDocument answer = AskHelper("opencode", path ?? "auto");
        JsonElement root = answer.RootElement;
        long Count(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number) && number >= 0 ? number
            : throw new ReadException(Failure.Invalid, "respuesta del lector sin " + name);
        decimal cost = root.TryGetProperty("cost", out JsonElement c) && c.ValueKind == JsonValueKind.String
            && decimal.TryParse(c.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) && parsed >= 0
            ? parsed : throw new ReadException(Failure.Invalid, "respuesta del lector sin coste");
        return new OpenCodeTotals(Count("input"), Count("output"), Count("reasoning"), Count("cacheRead"), Count("cacheWrite"), cost);
    }

    // ───────────────────────────── Elevated side ─────────────────────────────

    private static JsonDocument AskHelper(string kind, string path)
    {
        if (path != "auto" && (!Path.IsPathFullyQualified(path) || path.Contains('"')))
            throw new ReadException(Failure.Missing, "ruta no válida");
        string? exe = Environment.ProcessPath;
        if (exe is null) throw new ReadException(Failure.Unavailable, "sin ruta del ejecutable");

        UnelevatedLauncher.Result result = UnelevatedLauncher.Run(exe,
            $"{HelperArgument} {kind} {UnelevatedLauncher.Quote(path)}",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), hidden: true, wait: HelperTimeout,
            captureOutput: true, environment: new Dictionary<string, string> { ["__COMPAT_LAYER"] = "RunAsInvoker" });
        if (!result.Started) throw new ReadException(Failure.Unavailable, result.Error ?? "no se pudo iniciar el lector");
        if (result.TimedOut) throw new ReadException(Failure.Busy, "el lector tardó demasiado");
        string? output = result.RawOutput?.Trim();
        if (string.IsNullOrEmpty(output) || output.Length >= 8 * 1024 - 1)
            throw new ReadException(Failure.Unavailable, $"el lector no respondió (código {result.ExitCode?.ToString() ?? "?"})");

        JsonDocument answer;
        try { answer = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 2 }); }
        catch (JsonException) { throw new ReadException(Failure.Invalid, "respuesta del lector no válida"); }
        if (answer.RootElement.ValueKind != JsonValueKind.Object)
        {
            answer.Dispose();
            throw new ReadException(Failure.Invalid, "respuesta del lector no válida");
        }
        if (answer.RootElement.TryGetProperty("error", out JsonElement error))
        {
            string code = error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : "";
            answer.Dispose();
            throw code switch
            {
                "missing" => new ReadException(Failure.Missing, "no existe la base de datos"),
                "busy" => new ReadException(Failure.Busy, "base de datos ocupada"),
                "incompatible" => new ReadException(Failure.Incompatible, "versión no compatible"),
                _ => new ReadException(Failure.Invalid, "el lector no pudo leer la base de datos"),
            };
        }
        return answer;
    }

    // ───────────────────────────── Reading (helper or unelevated copy) ─────────────────────────────

    private static CursorSession? CursorInProcess(string path)
    {
        if (!File.Exists(path)) throw new ReadException(Failure.Missing, "no existe state.vscdb");
        string? token = null, membership = null;
        using (UntrustedSqlite connection = Open(path))
        {
            foreach (object?[] row in Query(connection,
                         "SELECT key, value FROM ItemTable WHERE key IN ('cursorAuth/accessToken', 'cursorAuth/stripeMembershipType')"))
            {
                if (row.Length != 2 || row[0] is not string key) continue;
                string? value = row[1] as string;
                if (key == "cursorAuth/accessToken") token = value;
                else membership = value;
            }
        }
        return string.IsNullOrWhiteSpace(token) ? null : new CursorSession(token, membership);
    }

    private static OpenCodeTotals OpenCodeInProcess(string? path)
    {
        string[] paths = path is not null and not "auto" ? new[] { path } : OpenCodeDatabasePaths();
        if (paths.Length == 0 || !paths.All(File.Exists)) throw new ReadException(Failure.Missing, "sin base de datos de sesiones");

        long input = 0, output = 0, reasoning = 0, cacheRead = 0, cacheWrite = 0;
        decimal cost = 0;
        foreach (string database in paths)
        {
            using UntrustedSqlite connection = Open(database);
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object?[] row in Query(connection, "PRAGMA table_info(session)"))
                if (row.Length > 1 && row[1] is string name) columns.Add(name);
            string[] required = { "tokens_input", "tokens_output", "tokens_reasoning", "tokens_cache_read", "tokens_cache_write", "cost" };
            if (required.Any(column => !columns.Contains(column)))
                throw new ReadException(Failure.Incompatible, "faltan columnas en session");

            List<object?[]> sums = Query(connection, "SELECT COALESCE(SUM(tokens_input), 0), COALESCE(SUM(tokens_output), 0), COALESCE(SUM(tokens_reasoning), 0), COALESCE(SUM(tokens_cache_read), 0), COALESCE(SUM(tokens_cache_write), 0), COALESCE(SUM(cost), 0) FROM session");
            if (sums.Count != 1 || sums[0].Length != 6) throw new ReadException(Failure.Invalid, "respuesta local sin datos");
            object?[] r = sums[0];
            input = checked(input + NonNegative(r[0]));
            output = checked(output + NonNegative(r[1]));
            reasoning = checked(reasoning + NonNegative(r[2]));
            cacheRead = checked(cacheRead + NonNegative(r[3]));
            cacheWrite = checked(cacheWrite + NonNegative(r[4]));
            double sessionCost = r[5] switch { double d => d, long l => l, _ => double.NaN };
            if (!double.IsFinite(sessionCost) || sessionCost < 0 || sessionCost > 1e12)
                throw new ReadException(Failure.Invalid, "coste no válido");
            cost += (decimal)sessionCost;
        }
        return new OpenCodeTotals(input, output, reasoning, cacheRead, cacheWrite, cost);
    }

    /// Token counts are whole and never negative; a database that says otherwise is not read.
    private static long NonNegative(object? value) => value is long number && number >= 0
        ? number : throw new ReadException(Failure.Invalid, "recuento de tokens no válido");

    private static UntrustedSqlite Open(string path)
    {
        try { return UntrustedSqlite.Open(path); }
        catch (UntrustedSqlite.SqliteFailure ex) when (ex.Busy) { throw new ReadException(Failure.Busy, "base de datos ocupada"); }
    }

    private static List<object?[]> Query(UntrustedSqlite connection, string sql)
    {
        try { return connection.Query(sql); }
        catch (UntrustedSqlite.SqliteFailure ex) when (ex.Busy) { throw new ReadException(Failure.Busy, "base de datos ocupada"); }
    }

    /// Where OpenCode keeps its database, in the user's own environment (the helper runs with it).
    private static string[] OpenCodeDatabasePaths()
    {
        string? overridePath = Environment.GetEnvironmentVariable("OPENCODE_DB");
        if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath))
            return File.Exists(overridePath) ? new[] { overridePath } : Array.Empty<string>();
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string[] directories =
        {
            Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(user, ".local", "share") : xdg, "opencode"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode"),
        };
        string? database = directories.Select(dir => Path.Combine(dir, "opencode.db")).FirstOrDefault(File.Exists);
        return database is null ? Array.Empty<string>() : new[] { Path.GetFullPath(database) };
    }

    // ───────────────────────────── Helper process ─────────────────────────────

    /// `OpenControlEdge.exe --read-database cursor|opencode <path|auto>`: one JSON object on stdout, exit code 0.
    /// Refuses to run elevated: the whole point is that the file is parsed without administrator rights.
    internal static int RunHelper(string[] args)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stdout);
        writer.WriteStartObject();
        try
        {
            if (UnelevatedLauncher.IsElevated) throw new ReadException(Failure.Unavailable, "elevated");
            if (args.Length != 3 || args[0] != HelperArgument) throw new ReadException(Failure.Invalid, "arguments");
            string path = args[2];
            if (path != "auto" && !Path.IsPathFullyQualified(path)) throw new ReadException(Failure.Missing, "path");
            switch (args[1])
            {
                case "cursor":
                    if (CursorInProcess(path) is CursorSession session)
                    {
                        writer.WriteString("token", session.Token);
                        if (session.Membership is not null) writer.WriteString("membership", session.Membership);
                    }
                    break;
                case "opencode":
                    OpenCodeTotals totals = OpenCodeInProcess(path);
                    writer.WriteNumber("input", totals.Input);
                    writer.WriteNumber("output", totals.Output);
                    writer.WriteNumber("reasoning", totals.Reasoning);
                    writer.WriteNumber("cacheRead", totals.CacheRead);
                    writer.WriteNumber("cacheWrite", totals.CacheWrite);
                    writer.WriteString("cost", totals.Cost.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new ReadException(Failure.Invalid, "kind");
            }
        }
        catch (ReadException ex)
        {
            writer.WriteString("error", ex.Kind switch
            {
                Failure.Missing => "missing", Failure.Busy => "busy", Failure.Incompatible => "incompatible", _ => "invalid",
            });
        }
        catch (Exception)
        {
            writer.WriteString("error", "invalid");
        }
        writer.WriteEndObject();
        writer.Flush();
        return 0;
    }
}
