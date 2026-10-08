using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using static OpenControlEdge.Interop.ProcessNative;

namespace OpenControlEdge.Services;

/// Starts programs as the signed-in user WITHOUT this process's administrator rights.
///
/// Elevated (the installed copy, started by its scheduled task): the shell's own token is borrowed — the process
/// behind GetShellWindow (explorer.exe of the interactive session) — duplicated as a primary token and handed to
/// CreateProcessWithTokenW, with the user's own environment block. It must belong to the same account as this
/// process and must not be elevated itself; otherwise nothing is started (never falls back to this process's token).
/// Not elevated (Debug, or a copy started by hand): CreateProcessW with this process's token, which already is the
/// plain user's. Everything else — command line, flags, hidden window, job and timeout — is the same code path.
///
/// Never throws: every failure is a Result with Error set (a short Spanish message for the log, never a secret).
internal static partial class UnelevatedLauncher
{
    internal sealed record Result(bool Started, uint? ExitCode, bool TimedOut, string? Error)
    {
        public static Result Fail(string error) => new(false, null, false, error);

        /// captureOutput: the first line the program wrote to stdout or stderr, sanitised (see Summarize).
        public string? Output { get; init; }

        /// captureOutput: everything it wrote (at most MaxCapturedBytes), unsanitised. Never logged: it may hold a
        /// token (SqliteHelper's answer).
        public string? RawOutput { get; init; }
    }

    /// CreateProcessWithTokenW goes through the Secondary Logon service; disabled (1058) or missing (1060), nothing
    /// can be started without this process's rights.
    public const string SeclogonMessage = "El servicio Inicio de sesión secundario (seclogon) está desactivado: actívalo para abrir programas sin permisos de administrador";

    public static bool IsElevated { get; } = IsCurrentProcessElevated();

    private const int MaxCapturedBytes = 8 * 1024;

    /// Starts application with arguments. hidden: no window at all (CREATE_NO_WINDOW, and SW_HIDE for anything that
    /// still asks for one). wait: waits up to that long for it to exit; on timeout the whole process tree is killed
    /// (it runs inside a kill-on-close job). Without wait it is fire-and-forget and outlives nothing of ours.
    /// captureOutput (with wait only): stdout and stderr go to a pipe and Result.Output keeps a sanitised first line.
    /// environment: variables added to (or replacing) the user's own environment for this program only.
    public static Result Run(string application, string arguments, string? workingDirectory, bool hidden, TimeSpan? wait,
        bool captureOutput = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        string commandLine = Quote(application) + (arguments.Length > 0 ? " " + arguments : string.Empty);
        uint flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | (hidden ? CREATE_NO_WINDOW : 0);
        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpDesktop = @"winsta0\default",
            dwFlags = hidden ? STARTF_USESHOWWINDOW : 0,
            wShowWindow = hidden ? SW_HIDE : (short)0,
        };

        using AnonymousPipeServerStream? pipe = captureOutput && wait is not null
            ? new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)
            : null;
        if (pipe is not null)
        {
            startup.dwFlags |= STARTF_USESTDHANDLES;
            startup.hStdOutput = startup.hStdError = pipe.ClientSafePipeHandle.DangerousGetHandle();
        }

        PROCESS_INFORMATION info;
        IntPtr token = IntPtr.Zero, userEnvironment = IntPtr.Zero, customEnvironment = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr currentToken))
                return Result.Fail($"sin token propio (error {Marshal.GetLastWin32Error()}); no se lanza nada");
            bool elevated;
            try
            {
                if (!GetTokenInformation(currentToken, TokenElevation, out int level, sizeof(int), out _))
                    return Result.Fail($"no se pudo comprobar el nivel del token (error {Marshal.GetLastWin32Error()}); no se lanza nada");
                elevated = level != 0;
            }
            finally { CloseHandle(currentToken); }
            if (elevated)
            {
                string? error = ShellToken(out token);
                if (error is not null) return Result.Fail(error);
                if (!CreateEnvironmentBlock(out userEnvironment, token, false))
                    return Result.Fail($"sin entorno del usuario (error {Marshal.GetLastWin32Error()})");
                if (environment is not null) customEnvironment = BuildEnvironment(ReadEnvironment(userEnvironment), environment);
                if (!CreateProcessWithTokenW(token, 0, application, commandLine, flags,
                        customEnvironment != IntPtr.Zero ? customEnvironment : userEnvironment, workingDirectory,
                        ref startup, out info))
                {
                    int code = Marshal.GetLastWin32Error();
                    return Result.Fail(code is 1058 or 1060 ? SeclogonMessage : $"CreateProcessWithTokenW error {code}");
                }
            }
            else
            {
                // Our own environment, but as a Unicode block like the elevated path: IntPtr.Zero inherits it.
                // Handles are inherited only for the output pipe.
                if (environment is not null)
                {
                    var own = new List<string>();
                    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                        own.Add(entry.Key + "=" + entry.Value);
                    customEnvironment = BuildEnvironment(own, environment);
                }
                if (!CreateProcessW(application, commandLine, IntPtr.Zero, IntPtr.Zero, pipe is not null, flags, customEnvironment,
                        workingDirectory, ref startup, out info))
                    return Result.Fail($"CreateProcessW error {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            if (userEnvironment != IntPtr.Zero) DestroyEnvironmentBlock(userEnvironment);
            if (customEnvironment != IntPtr.Zero) Marshal.FreeHGlobal(customEnvironment);
            if (token != IntPtr.Zero) CloseHandle(token);
        }

        if (wait is not TimeSpan limit) return Release(info);
        if (pipe is null) return RunInJob(info, limit);

        // Our copy of the write end must go, or the read below never sees the end of the stream.
        pipe.DisposeLocalCopyOfClientHandle();
        Task<string> output = Task.Run(() => ReadCapped(pipe));
        Result result = RunInJob(info, limit);
        string? captured = null;
        try { if (output.Wait(TimeSpan.FromSeconds(2))) captured = output.Result; }
        catch (AggregateException) { }
        return result with { Output = Summarize(captured), RawOutput = captured };
    }

    /// The NAME=value strings of a Unicode environment block (each ends in a NUL, the block in a second one).
    private static List<string> ReadEnvironment(IntPtr block)
    {
        var entries = new List<string>();
        for (IntPtr at = block; ;)
        {
            string? entry = Marshal.PtrToStringUni(at);
            if (string.IsNullOrEmpty(entry)) return entries;
            entries.Add(entry);
            at += (entry.Length + 1) * sizeof(char);
        }
    }

    /// A new Unicode environment block (freed with Marshal.FreeHGlobal): entries, with every variable of overrides
    /// replacing one of the same name (names compare without case, as Windows does).
    private static IntPtr BuildEnvironment(List<string> entries, IReadOnlyDictionary<string, string> overrides)
    {
        var text = new System.Text.StringBuilder();
        foreach (string entry in entries)
        {
            int equals = entry.IndexOf('=', 1);
            string name = equals > 0 ? entry[..equals] : entry;
            if (overrides.Keys.Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            text.Append(entry).Append('\0');
        }
        foreach ((string name, string value) in overrides) text.Append(name).Append('=').Append(value).Append('\0');
        text.Append('\0');
        return Marshal.StringToHGlobalUni(text.ToString());
    }

    /// Reads the pipe to the end, keeping the first MaxCapturedBytes (the rest is drained so the program never blocks).
    private static string ReadCapped(Stream stream)
    {
        var kept = new MemoryStream();
        byte[] buffer = new byte[4096];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                int room = MaxCapturedBytes - (int)kept.Length;
                if (room > 0) kept.Write(buffer, 0, Math.Min(room, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        return System.Text.Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
    }

    /// The first non-empty line, for the log: at most 160 characters, and every run of 24 or more token-like
    /// characters (keys, tokens, hashes) replaced with "…", so no secret ever reaches the log.
    internal static string? Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        line = SecretLike().Replace(line, "…");
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Za-z0-9_\-\.+/=]{24,}")]
    private static partial System.Text.RegularExpressions.Regex SecretLike();

    /// Fire-and-forget: let it run and drop our handles.
    private static Result Release(PROCESS_INFORMATION info)
    {
        if (ResumeThread(info.hThread) == uint.MaxValue)
        {
            TerminateProcess(info.hProcess, 1);
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return Result.Fail($"no se pudo reanudar el proceso (error {Marshal.GetLastWin32Error()})");
        }
        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        return new Result(true, null, false, null);
    }

    /// Runs the (still suspended) process inside a kill-on-close job, so a timeout also ends every child it started
    /// (node, hooks…), and waits for it.
    private static Result RunInJob(PROCESS_INFORMATION info, TimeSpan limit)
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            bool inJob = job != IntPtr.Zero
                         && SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits))
                         && AssignProcessToJobObject(job, info.hProcess);
            if (!inJob) Log.Warn("Launcher", $"sin job (error {Marshal.GetLastWin32Error()}); solo se podrá cerrar el proceso principal");

            if (ResumeThread(info.hThread) == uint.MaxValue)
            {
                TerminateProcess(info.hProcess, 1);
                return Result.Fail($"no se pudo reanudar el proceso (error {Marshal.GetLastWin32Error()})");
            }
            uint waited = WaitForSingleObject(info.hProcess, (uint)Math.Min(limit.TotalMilliseconds, uint.MaxValue - 1));
            if (waited == WAIT_TIMEOUT)
            {
                if (inJob) TerminateJobObject(job, 1);
                else TerminateProcess(info.hProcess, 1);
                return new Result(true, null, true, null);
            }

            return GetExitCodeProcess(info.hProcess, out uint code)
                ? new Result(true, code, false, null)
                : new Result(true, null, false, null);
        }
        finally
        {
            if (job != IntPtr.Zero) CloseHandle(job);
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
    }

    /// A primary token of the interactive shell, checked to be the same account as ours and not elevated.
    /// Returns null on success, otherwise why not.
    private static string? ShellToken(out IntPtr primary)
    {
        primary = IntPtr.Zero;
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return "no hay escritorio del usuario (explorer.exe no está en marcha)";
        GetWindowThreadProcessId(shell, out uint pid);
        if (pid == 0) return "no se encuentra el proceso del escritorio";

        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return $"no se puede abrir el escritorio (error {Marshal.GetLastWin32Error()})";
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY | TOKEN_DUPLICATE, out token))
                return $"sin token del escritorio (error {Marshal.GetLastWin32Error()})";

            using (var shellUser = new WindowsIdentity(token))
            using (var self = WindowsIdentity.GetCurrent())
            {
                if (shellUser.User is null || self.User is null || !shellUser.User.Equals(self.User))
                    return "el escritorio es de otra cuenta; no se lanza nada";
            }

            // UAC off: the shell itself is elevated, so there is no plain-user token to hand out.
            if (IsTokenElevated(token)) return "el escritorio corre como administrador; no se lanza nada";
            if (!IsMediumIntegrity(token)) return "el escritorio no tiene nivel de integridad medio; no se lanza nada";

            const uint access = TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(token, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                return $"no se puede duplicar el token (error {Marshal.GetLastWin32Error()})";
            if (!IsMediumIntegrity(primary))
            {
                CloseHandle(primary);
                primary = IntPtr.Zero;
                return "el token duplicado no tiene nivel de integridad medio; no se lanza nada";
            }
            return null;
        }
        catch (Exception ex)
        {
            return "token del escritorio: " + ex.GetType().Name;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    private static bool IsMediumIntegrity(IntPtr token)
    {
        const int BufferSize = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, BufferSize, out _)) return false;
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) return false;
            IntPtr countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero) return false;
            byte count = Marshal.ReadByte(countPointer);
            if (count == 0) return false;
            IntPtr rid = GetSidSubAuthority(sid, (uint)(count - 1));
            return rid != IntPtr.Zero && Marshal.ReadInt32(rid) == 0x2000;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// The interactive desktop belongs to the account this process runs as (ported from instalar.ps1, which refused
    /// to install when the UAC prompt had been accepted with a different administrator account).
    public static bool ShellBelongsToThisAccount()
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) return false;
        GetWindowThreadProcessId(shell, out uint pid);
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY | TOKEN_DUPLICATE, out IntPtr token)) return false;
            try
            {
                using var shellUser = new WindowsIdentity(token);
                using var self = WindowsIdentity.GetCurrent();
                return shellUser.User is not null && self.User is not null && shellUser.User.Equals(self.User);
            }
            finally { CloseHandle(token); }
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
        finally { CloseHandle(process); }
    }

    /// Quotes a path for a command line (paths never contain quotes on Windows).
    public static string Quote(string path) => "\"" + path + "\"";
}
