using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace OpenControlEdge.Services;

/// "Iniciar con Windows": the scheduled task OpenControlEdge, which starts the installed copy with administrator
/// rights (needed by the CPU sensors) when the interactive user signs in — with no delay and normal priority (tasks
/// default to priority 7, below normal, which slows the first paint) — and without a UAC prompt.
///
/// The task only ever starts C:\Program Files\OpenControlEdge\OpenControlEdge.exe, where only administrators can
/// write: an elevated autostart pointing at a folder the user can write to would hand those rights to any program
/// that replaces the file. It runs it through start.cmd in that same folder (System32's conhost --headless and cmd /d,
/// no window): the task inherits the user's environment, which any program of the user can change in
/// HKCU\Environment, and the .NET runtime reads CORECLR_* / COR_* (a profiler DLL loaded before Main), DOTNET_* and
/// COMPlus_* (diagnostic ports, dumps written anywhere…) from it. start.cmd clears every variable with those prefixes
/// and sets PATH to Windows' own folders before starting the widget, so none of them reaches the elevated process. schtasks.exe reads the task definition from an XML file written inside that same folder
/// (never a temp folder the user could tamper with between the write and the read) and deleted right after.
///
/// Every method returns null on success or a Spanish message; none throws.
internal static class AutoStartService
{
    public const string TaskName = "OpenControlEdge";

    /// Tasks of earlier versions: removed by the installer.
    internal const string LegacyTaskName = "EdgeWidget";
    internal const string LegacyClaudeTaskName = "Claude - Mantener sesion";

    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// The launcher the task runs (see the class): written by the installed copy itself, elevated.
    public static string StartScript => Path.Combine(Installer.InstallDir, "start.cmd");

    private static string Conhost => Path.Combine(Environment.SystemDirectory, "conhost.exe");
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static string TaskArguments => $"--headless \"{Cmd}\" /d /c \"{StartScript}\"";

    /// start.cmd as it must be: the prefixes cleared one by one ("set PREFIX" lists them), then the widget started.
    private static string StartScriptText
    {
        get
        {
            string windows = Path.GetDirectoryName(Environment.SystemDirectory) ?? @"C:\Windows";
            var text = new StringBuilder();
            text.Append("@echo off\r\n");
            text.Append("rem Open Control Edge: starts the installed copy without the .NET runtime variables of the user's environment.\r\n");
            text.Append("setlocal\r\n");
            foreach (string prefix in new[] { "DOTNET_", "COMPlus_", "CORECLR_", "COR_" })
                text.Append($"for /f \"delims==\" %%V in ('set {prefix} 2^>nul') do set \"%%V=\"\r\n");
            text.Append($"set \"PATH={Environment.SystemDirectory};{windows};{Path.Combine(Environment.SystemDirectory, "Wbem")}\"\r\n");
            text.Append($"start \"\" \"{Installer.InstalledExe}\"\r\n");
            return text.ToString();
        }
    }

    /// Writes start.cmd when it is missing or different (an update replaces the whole folder). Elevated only, and only
    /// into the protected install folder. Null on success or a Spanish message.
    internal static string? EnsureStartScript()
    {
        try
        {
            if (!UnelevatedLauncher.IsElevated) return "Hace falta ejecutar como administrador";
            if (!Directory.Exists(Installer.InstallDir) || DataFolder.NonAdminsCanWrite(Installer.InstallDir, directory: true))
                return "La carpeta de instalación no está protegida; no se registra el inicio";
            string text = StartScriptText;
            if (File.Exists(StartScript) && (File.GetAttributes(StartScript) & FileAttributes.ReparsePoint) == 0
                && File.ReadAllText(StartScript, Encoding.ASCII) == text) return null;
            string temporary = StartScript + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Encoding.ASCII))
                writer.Write(text);
            File.Move(temporary, StartScript, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("AutoStart", "start.cmd: " + ex.GetType().Name);
            return "No se pudo registrar el inicio con Windows";
        }
    }

    /// The installed copy, once per start: start.cmd is (re)written and a task of an earlier version, which started the
    /// executable straight away, is registered again through it. Never throws.
    internal static void Upgrade()
    {
        try
        {
            if (!Installer.IsInstalledCopy || !UnelevatedLauncher.IsElevated) return;
            if (EnsureStartScript() is string error) { Log.Warn("AutoStart", error); return; }
            if (QueryCommand() is (string command, _) && string.Equals(command.Trim('"'), Installer.InstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                string? result = Register();
                Log.Info("AutoStart", result is null ? "tarea actualizada: arranca mediante start.cmd" : "no se pudo actualizar la tarea: " + result);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("AutoStart", "actualizar la tarea: " + ex.GetType().Name);
        }
    }

    /// The command and arguments of the task; null when it does not exist or cannot be read.
    private static (string Command, string Arguments)? QueryCommand()
    {
        if (RunSchtasks($"/Query /TN \"{TaskName}\" /XML ONE", out string xml, QueryTimeoutMs) != SchtasksRun.Ok) return null;
        XDocument document = XDocument.Parse(xml.TrimStart('\uFEFF'));
        XNamespace ns = TaskNamespace;
        XElement? exec = document.Root?.Element(ns + "Actions")?.Element(ns + "Exec");
        string? command = exec?.Element(ns + "Command")?.Value;
        return command is null ? null : (command, exec?.Element(ns + "Arguments")?.Value ?? "");
    }

    /// The task starts the installed copy: through start.cmd, or (earlier versions) the executable itself.
    private static bool StartsInstalledCopy(string command, string arguments) =>
        string.Equals(command.Trim('"'), Installer.InstalledExe, StringComparison.OrdinalIgnoreCase)
        || (string.Equals(command.Trim('"'), Conhost, StringComparison.OrdinalIgnoreCase)
            && arguments.Contains(StartScript, StringComparison.OrdinalIgnoreCase));

    /// The task exists, is enabled and starts the installed copy.
    public static bool IsEnabled() => Query().Enabled;

    /// Enabled as IsEnabled; Error says why the answer could not be read (then Enabled is false).
    /// A missing task is a plain "off", not an error.
    public static (bool Enabled, string? Error) Query()
    {
        try
        {
            var clock = Stopwatch.StartNew();
            SchtasksRun run = RunSchtasks($"/Query /TN \"{TaskName}\" /XML ONE", out string xml, QueryTimeoutMs);
            Log.Trace("AutoStart", $"consulta: {run} en {clock.ElapsedMilliseconds} ms");
            if (run == SchtasksRun.TimedOut) return (false, QueryTimeoutMessage);
            if (run == SchtasksRun.NotStarted) return (false, QueryFailedMessage);
            if (run == SchtasksRun.Failed) return (false, null);
            XDocument document = XDocument.Parse(xml.TrimStart('\uFEFF'));
            XNamespace ns = TaskNamespace;
            string? enabled = document.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
            XElement? exec = document.Root?.Element(ns + "Actions")?.Element(ns + "Exec");
            string? command = exec?.Element(ns + "Command")?.Value;
            return (!string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase)
                    && command is not null
                    && StartsInstalledCopy(command, exec?.Element(ns + "Arguments")?.Value ?? ""), null);
        }
        catch (Exception ex)
        {
            Log.Warn("AutoStart", "consulta: " + ex.GetType().Name);
            return (false, QueryFailedMessage);
        }
    }

    public const string QueryTimeoutMessage = "El Programador de tareas no ha respondido en 5 s";
    public const string QueryFailedMessage = "No se pudo consultar el inicio con Windows";
    private const int QueryTimeoutMs = 5_000;

    public static string? Enable()
    {
        if (!UnelevatedLauncher.IsElevated) return "Hace falta ejecutar como administrador";
        if (!File.Exists(Installer.InstalledExe)) return "Instala Open Control Edge primero";
        return Register();
    }

    public static string? Disable()
    {
        if (!UnelevatedLauncher.IsElevated) return "Hace falta ejecutar como administrador";
        return Delete(TaskName) ? null : "No se pudo quitar el inicio con Windows";
    }

    /// Removes the task, closes any other copy and schedules the install folder for deletion at the next restart (the
    /// running executable cannot be deleted before). Settings, cache and log are kept in the data folder, which is
    /// given back to the user (DataFolder.Release) so they can be opened or deleted without administrator rights.
    public static string? Uninstall()
    {
        if (!UnelevatedLauncher.IsElevated) return "Hace falta ejecutar como administrador";
        try
        {
            Stop(TaskName);
            Delete(TaskName);
            Installer.StopOtherInstances();
            if (Directory.Exists(Installer.InstallDir)) Installer.DeleteAtRestart(Installer.InstallDir);
            Log.Info("AutoStart", "desinstalado: tarea quitada y carpeta marcada para borrarse al reiniciar");
            DataFolder.Release();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall", ex);
            return "No se pudo desinstalar";
        }
    }

    /// Creates (or replaces) the task that starts the installed executable through start.cmd. Elevated only.
    internal static string? Register()
    {
        if (EnsureStartScript() is string scriptError) return scriptError;
        using var identity = WindowsIdentity.GetCurrent();
        string user = SecurityElement.Escape(identity.Name) ?? identity.Name;
        string exe = SecurityElement.Escape(Conhost) ?? Conhost;
        string arguments = SecurityElement.Escape(TaskArguments) ?? TaskArguments;
        string dir = SecurityElement.Escape(Installer.InstallDir) ?? Installer.InstallDir;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="{TaskNamespace}">
              <RegistrationInfo>
                <Author>Open Control Edge</Author>
                <Description>Starts Open Control Edge when the user signs in.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{arguments}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        string file = Path.Combine(Installer.InstallDir, $"task-{Guid.NewGuid():N}.xml");
        try
        {
            if (DataFolder.NonAdminsCanWrite(Installer.InstallDir, directory: true))
                return "La carpeta de instalación no está protegida; no se registra el inicio";
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UnicodeEncoding(false, true)))
                writer.Write(xml);
            if (!RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out _)) return "No se pudo registrar el inicio con Windows";
            Log.Info("AutoStart", $"tarea '{TaskName}' -> {StartScript} -> {Installer.InstalledExe}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("AutoStart", "registro: " + ex.GetType().Name + ": " + ex.Message);
            return "No se pudo registrar el inicio con Windows";
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool Exists(string name) => RunSchtasks($"/Query /TN \"{name}\"", out _);

    internal static bool Run(string name) => RunSchtasks($"/Run /TN \"{name}\"", out _);

    internal static void Stop(string name)
    {
        if (Exists(name)) RunSchtasks($"/End /TN \"{name}\"", out _);
    }

    /// True when the task is gone (or never existed).
    internal static bool Delete(string name) => !Exists(name) || RunSchtasks($"/Delete /TN \"{name}\" /F", out _);

    private enum SchtasksRun { Ok, Failed, TimedOut, NotStarted }

    /// schtasks.exe from System32, no window, 15 s at most. True on exit code 0.
    private static bool RunSchtasks(string arguments, out string output) =>
        RunSchtasks(arguments, out output, 15_000) == SchtasksRun.Ok;

    private static SchtasksRun RunSchtasks(string arguments, out string output, int timeoutMs)
    {
        output = string.Empty;
        var start = new ProcessStartInfo(Schtasks, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process? process;
        try { process = Process.Start(start); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return SchtasksRun.NotStarted; }
        if (process is null) return SchtasksRun.NotStarted;
        using (process)
        {
            Task<string> read = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return SchtasksRun.TimedOut;
            }
            output = read.Result;
            return process.ExitCode == 0 ? SchtasksRun.Ok : SchtasksRun.Failed;
        }
    }
}
