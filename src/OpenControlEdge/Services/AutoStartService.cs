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
/// The task only ever points at C:\Program Files\OpenControlEdge\OpenControlEdge.exe, where only administrators can
/// write: an elevated autostart pointing at a folder the user can write to would hand those rights to any program
/// that replaces the file. schtasks.exe reads the task definition from an XML file written inside that same folder
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

    /// The task exists, is enabled and starts the installed copy.
    public static bool IsEnabled()
    {
        try
        {
            if (!RunSchtasks($"/Query /TN \"{TaskName}\" /XML ONE", out string xml)) return false;
            XDocument document = XDocument.Parse(xml.TrimStart('\uFEFF'));
            XNamespace ns = TaskNamespace;
            string? enabled = document.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
            string? command = document.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value;
            return !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase)
                   && command is not null
                   && string.Equals(command.Trim('"'), Installer.InstalledExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("AutoStart", "consulta: " + ex.GetType().Name);
            return false;
        }
    }

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
    /// running executable cannot be deleted before). Settings, cache and log in %LOCALAPPDATA% are kept.
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
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall", ex);
            return "No se pudo desinstalar";
        }
    }

    /// Creates (or replaces) the task pointing at the installed executable. Elevated only.
    internal static string? Register()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string user = SecurityElement.Escape(identity.Name) ?? identity.Name;
        string exe = SecurityElement.Escape(Installer.InstalledExe) ?? Installer.InstalledExe;
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
            Log.Info("AutoStart", $"tarea '{TaskName}' -> {Installer.InstalledExe}");
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

    /// schtasks.exe from System32, no window, 15 s at most. True on exit code 0.
    private static bool RunSchtasks(string arguments, out string output)
    {
        output = string.Empty;
        var start = new ProcessStartInfo(Schtasks, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process? process = Process.Start(start);
        if (process is null) return false;
        Task<string> read = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return false;
        }
        output = read.Result;
        return process.ExitCode == 0;
    }
}
