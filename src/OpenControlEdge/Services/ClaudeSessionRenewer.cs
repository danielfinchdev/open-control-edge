using System.IO;

namespace OpenControlEdge.Services;

internal enum RenewOutcome
{
    /// expiresAt moved forward: the CLI refreshed the token.
    Renewed,

    /// The CLI ran fine but kept the token: it only refreshes it within its last 5 minutes.
    StillValid,

    /// The CLI ran and the token is still expired (or unreadable): the user has to sign in again.
    NotRenewed,

    /// There is no sign-in left to refresh (see CredentialReader.HasSignIn): the CLI is not even run.
    SignedOut,
    CliMissing,
    TimedOut,
    Failed,
}

/// Error: why the CLI could not be started (UnelevatedLauncher), for the note under the card.
internal sealed record RenewResult(RenewOutcome Outcome, DateTimeOffset? ExpiresAt, string? Error = null);

/// Renews the Claude Code session without opening any window: runs the CLI once, as the plain user (see
/// UnelevatedLauncher), with the smallest request that makes it check the token — no tools, no MCP servers, no
/// saved session, Haiku. The CLI refreshes the OAuth token itself and rewrites .credentials.json; this class never
/// touches that file and only reads its expiry, before and after.
///
/// Verified 2026-09-29 on Claude Code 2.1.284: the CLI refreshes only when now + 300000 ms >= expiresAt, i.e. in the
/// last 5 minutes of the token (earlier runs leave it as it is).
internal static class ClaudeSessionRenewer
{
    public const string CliMissingMessage = "No se encuentra Claude Code: instálalo para renovar desde aquí";
    public const string TimedOutMessage = "Claude Code no ha respondido en 60 s";
    public const string StartFailedMessage = "No se pudo iniciar Claude Code sin privilegios de administrador";
    public const string NotRenewedMessage = "Claude Code no ha renovado la sesión: ábrelo e inicia sesión";
    public const string SignedOutMessage = "Sin sesión en Claude Code: pulsa el anillo para iniciar sesión";
    public const string LoginOpenedMessage = "Inicia sesión en la ventana de Claude Code; el anillo se actualizará solo";

    /// The CLI refreshes within this margin of the expiry (see above).
    public static readonly TimeSpan CliRefreshWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// -p: one answer and exit. --tools "": no tools at all. --strict-mcp-config: no MCP servers from any config.
    /// --no-session-persistence: nothing saved to resume. The prompt is a single word.
    private const string Arguments = "-p --no-session-persistence --model haiku --tools \"\" --strict-mcp-config ok";

    /// Signing in again: the CLI's own browser flow, in a console window of its own (verified on 2.1.285).
    private const string LoginArguments = "auth login";

    internal sealed record Command(string Application, string Arguments);

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string NpmPackage => Path.Combine(AppData, "npm", "node_modules", "@anthropic-ai", "claude-code");

    /// The Claude Code CLI on this PC, or null. In order:
    ///   %USERPROFILE%\.local\bin\claude.exe                      native installer
    ///   %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe   npm package, native binary (verified
    ///                                                           2026-09-29: 2.1.284 ships it, no cli.js)
    ///   node + …\claude-code\cli.js                               older npm packages (JavaScript)
    /// Paths come from this process's profile: UnelevatedLauncher only runs it for the same account.
    public static Command? Locate() => Locate(Arguments);

    private static Command? Locate(string arguments)
    {
        foreach (string exe in new[]
                 {
                     Path.Combine(UserProfile, ".local", "bin", "claude.exe"),
                     Path.Combine(NpmPackage, "bin", "claude.exe"),
                 })
        {
            if (IsNativeExecutable(exe)) return new Command(exe, arguments);
        }

        string script = Path.Combine(NpmPackage, "cli.js");
        if (File.Exists(script) && FindNode() is string node)
            return new Command(node, UnelevatedLauncher.Quote(script) + " " + arguments);
        return null;
    }

    /// The single way to sign in again (the ring when signed out, and "Conectar" in Settings): `claude auth login`
    /// in a visible console, as the plain user. Null when it started, otherwise the reason (a service message).
    public static string? StartLogin()
    {
        try
        {
            Command? cli = Locate(LoginArguments);
            if (cli is null) return CliMissingMessage;
            UnelevatedLauncher.Result run = UnelevatedLauncher.Run(cli.Application, cli.Arguments, UserProfile, hidden: false, wait: null);
            Log.Info("Claude", run.Started ? "inicio de sesión abierto (claude auth login)" : "no se abrió el inicio de sesión: " + run.Error);
            return run.Started ? null : run.Error == UnelevatedLauncher.SeclogonMessage ? run.Error : StartFailedMessage;
        }
        catch (Exception ex)
        {
            Log.Error("Claude login", ex);
            return StartFailedMessage;
        }
    }

    /// See CredentialReader.HasSignIn.
    public static bool HasSignIn => CredentialReader.HasSignIn(CredentialReader.DefaultPath);

    /// A real PE file ("MZ", over 1 MB) — not the small placeholder an npm install leaves when its postinstall
    /// did not run.
    private static bool IsNativeExecutable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1024 * 1024) return false;
            using FileStream stream = info.OpenRead();
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? FindNode()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string> { Path.Combine(programFiles, "nodejs", "node.exe") };
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Path.IsPathFullyQualified(segment)) candidates.Add(Path.Combine(segment, "node.exe"));
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsAvailable => Locate() is not null;

    /// Token expiry from .credentials.json (only expiresAt is looked at), or null.
    public static DateTimeOffset? ReadExpiry()
    {
        try { return CredentialReader.Read(CredentialReader.DefaultPath)?.ExpiresAt; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Claude", "credenciales no legibles: " + ex.GetType().Name);
            return null;
        }
    }

    /// Runs the CLI once and reports what happened to the token. Runs on a worker thread; never throws.
    public static Task<RenewResult> RenewAsync() => Task.Run(Renew);

    private static RenewResult Renew()
    {
        try
        {
            Command? cli = Locate();
            if (cli is null) return new RenewResult(RenewOutcome.CliMissing, ReadExpiry());
            if (!HasSignIn)
            {
                Log.Info("Claude", "sin sesión en Claude Code (token y renovación vacíos): no se ejecuta la CLI");
                return new RenewResult(RenewOutcome.SignedOut, null);
            }

            DateTimeOffset? before = ReadExpiry();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            UnelevatedLauncher.Result run = UnelevatedLauncher.Run(cli.Application, cli.Arguments, UserProfile, hidden: true, Timeout,
                captureOutput: true);
            DateTimeOffset? after = ReadExpiry();
            // The output is only logged when the CLI failed: then it is its error message, sanitised (no tokens).
            Log.Info("Claude", $"CLI {(run.Started ? "ejecutada" : "no iniciada")} en {clock.ElapsedMilliseconds} ms"
                               + (run.ExitCode is uint code ? $", código {code}" : string.Empty)
                               + (run.TimedOut ? ", tiempo agotado" : string.Empty)
                               + (run.Error is null ? string.Empty : ": " + run.Error)
                               + (UnelevatedLauncher.IsElevated ? " (token del escritorio)" : " (sin elevar)")
                               + (run.ExitCode is not (null or 0) && run.Output is string output ? " · salida: " + output : string.Empty));

            if (!run.Started) return new RenewResult(RenewOutcome.Failed, after, run.Error);
            if (run.ExitCode is not (null or 0) && !HasSignIn) return new RenewResult(RenewOutcome.SignedOut, null);
            if (run.TimedOut) return new RenewResult(RenewOutcome.TimedOut, after);
            if (after is DateTimeOffset newExpiry && (before is null || newExpiry > before.Value))
                return new RenewResult(RenewOutcome.Renewed, newExpiry);
            if (after is DateTimeOffset expiry && expiry > DateTimeOffset.UtcNow && run.ExitCode == 0)
                return new RenewResult(RenewOutcome.StillValid, expiry);
            return new RenewResult(RenewOutcome.NotRenewed, after);
        }
        catch (Exception ex)
        {
            Log.Error("Claude renew", ex);
            return new RenewResult(RenewOutcome.Failed, null);
        }
    }
}
