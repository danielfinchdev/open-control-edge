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
internal static class UnelevatedLauncher
{
    internal sealed record Result(bool Started, uint? ExitCode, bool TimedOut, string? Error)
    {
        public static Result Fail(string error) => new(false, null, false, error);
    }

    public static bool IsElevated { get; } = IsCurrentProcessElevated();

    /// Starts application with arguments. hidden: no window at all (CREATE_NO_WINDOW, and SW_HIDE for anything that
    /// still asks for one). wait: waits up to that long for it to exit; on timeout the whole process tree is killed
    /// (it runs inside a kill-on-close job). Without wait it is fire-and-forget and outlives nothing of ours.
    public static Result Run(string application, string arguments, string? workingDirectory, bool hidden, TimeSpan? wait)
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

        PROCESS_INFORMATION info;
        IntPtr token = IntPtr.Zero, environment = IntPtr.Zero;
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
                if (!CreateEnvironmentBlock(out environment, token, false))
                    return Result.Fail($"sin entorno del usuario (error {Marshal.GetLastWin32Error()})");
                if (!CreateProcessWithTokenW(token, 0, application, commandLine, flags, environment, workingDirectory,
                        ref startup, out info))
                    return Result.Fail($"CreateProcessWithTokenW error {Marshal.GetLastWin32Error()}");
            }
            else
            {
                // Our own environment, but as a Unicode block like the elevated path: IntPtr.Zero inherits it.
                if (!CreateProcessW(application, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                        workingDirectory, ref startup, out info))
                    return Result.Fail($"CreateProcessW error {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (token != IntPtr.Zero) CloseHandle(token);
        }

        return wait is TimeSpan limit ? RunInJob(info, limit) : Release(info);
    }

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

            const uint access = TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(token, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                return $"no se puede duplicar el token (error {Marshal.GetLastWin32Error()})";
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
