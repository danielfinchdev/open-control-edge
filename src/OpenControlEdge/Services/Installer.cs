using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using static OpenControlEdge.Interop.ProcessNative;
using static OpenControlEdge.Interop.SecurityNative;

namespace OpenControlEdge.Services;

internal enum InstallStep
{
    Stop,
    Copy,
    Data,
    Task,
    Start,
}

/// Ok: everything done (Warnings may still say something); otherwise Error says why and nothing was left half done.
internal sealed record InstallResult(bool Ok, string? Error, IReadOnlyList<string> Warnings);

/// Native replacement of tools\instalar.ps1, run by the welcome window with the single UAC prompt the executable
/// already asks for (its manifest requires administrator):
///
///   1. stops the widget (Open Control Edge and the old EdgeWidget) and their tasks;
///   2. copies the application folder to C:\Program Files\OpenControlEdge through a staging folder, checks every file
///      with SHA-256 and swaps it in (the previous copy comes back if the swap fails), then checks that no
///      non-administrator can write to the folder or the executable;
///   3. protects %LOCALAPPDATA%\OpenControlEdge (DataFolder.Harden: no links, admins-only writes, High label) and
///      brings the settings of earlier versions over;
///   4. registers the sign-in task (AutoStartService) and removes the old ones: EdgeWidget, and
///      «Claude - Mantener sesion», whose script is no longer shipped. C:\Program Files\EdgeWidget is left alone;
///   5. starts the installed copy through its task.
internal static class Installer
{
    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenControlEdge");

    public static string InstalledExe { get; } = Path.Combine(InstallDir, "OpenControlEdge.exe");

    private static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenControlEdge.exe");

    /// This process is the installed copy.
    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(CurrentExe), InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static Task<InstallResult> InstallAsync(IProgress<InstallStep> progress) => Task.Run(() => Install(progress));

    private static InstallResult Install(IProgress<InstallStep> progress)
    {
        var warnings = new List<string>();
        try
        {
            if (!UnelevatedLauncher.IsElevated) return Fail("Hace falta ejecutar como administrador.");
            if (IsInstalledCopy) return Fail("Esta ya es la copia instalada.");
            string sourceDir = Path.GetDirectoryName(Path.GetFullPath(CurrentExe))!;
            if (!UnelevatedLauncher.ShellBelongsToThisAccount())
                return Fail("La instalación debe aceptarse con la misma cuenta que tiene la sesión abierta.");

            // Settings of earlier versions, looked up before the old install folder is replaced.
            string? oldSettings = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeWidget", "EdgeWidget.settings.json"),
                Path.Combine(sourceDir, SettingsFileName),
                Path.Combine(InstallDir, SettingsFileName),
            }.FirstOrDefault(File.Exists);
            byte[]? oldSettingsBytes = oldSettings is null ? null : File.ReadAllBytes(oldSettings);

            progress.Report(InstallStep.Stop);
            AutoStartService.Stop(AutoStartService.TaskName);
            AutoStartService.Stop(AutoStartService.LegacyTaskName);
            StopOtherInstances();
            if (Directory.Exists(Path.Combine(Path.GetDirectoryName(InstallDir)!, "EdgeWidget")))
                warnings.Add("Se conserva la carpeta antigua C:\\Program Files\\EdgeWidget.");

            progress.Report(InstallStep.Copy);
            CopyApplication(sourceDir);
            if (DataFolder.NonAdminsCanWrite(InstallDir, directory: true) || DataFolder.NonAdminsCanWrite(InstalledExe, directory: false))
                warnings.Add("Usuarios sin privilegios pueden escribir en la carpeta de instalación.");

            progress.Report(InstallStep.Data);
            PrepareDataFolder(oldSettingsBytes, warnings);

            progress.Report(InstallStep.Task);
            if (AutoStartService.Register() is string taskError) return Fail(taskError);
            foreach (string legacy in new[] { AutoStartService.LegacyTaskName, AutoStartService.LegacyClaudeTaskName })
            {
                if (!AutoStartService.Exists(legacy)) continue;
                AutoStartService.Stop(legacy);
                if (AutoStartService.Delete(legacy)) Log.Info("Install", $"tarea antigua '{legacy}' eliminada");
                else warnings.Add($"No se pudo quitar la tarea antigua «{legacy}».");
            }

            progress.Report(InstallStep.Start);
            if (!AutoStartService.Run(AutoStartService.TaskName))
            {
                // The task is registered; starting the copy directly keeps this session going anyway.
                Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir })?.Dispose();
            }

            Log.Info("Install", $"instalado en {InstallDir}" + (warnings.Count > 0 ? " con avisos: " + string.Join(" ", warnings) : string.Empty));
            return new InstallResult(true, null, warnings);
        }
        catch (Exception ex)
        {
            Log.Error("Install", ex);
            return Fail(ex is InvalidOperationException or IOException or UnauthorizedAccessException
                ? ex.Message : "Error inesperado durante la instalación.");
        }

        InstallResult Fail(string message)
        {
            Log.Warn("Install", message);
            return new InstallResult(false, message, warnings);
        }
    }

    private const string SettingsFileName = "OpenControlEdge.settings.json";

    /// Ends every other Open Control Edge and EdgeWidget process and waits up to 5 s for them to go.
    internal static void StopOtherInstances()
    {
        int self = Environment.ProcessId;
        var stopping = new List<Process>();
        foreach (string name in new[] { "OpenControlEdge", "EdgeWidget" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (process.Id == self) { process.Dispose(); continue; }
                try { process.Kill(); stopping.Add(process); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { process.Dispose(); }
            }
        }
        foreach (Process process in stopping)
        {
            using (process) process.WaitForExit(5_000);
        }
    }

    /// Staging copy, SHA-256 of every file, then an atomic-as-possible swap with rollback.
    private static void CopyApplication(string sourceDir)
    {
        string staging = InstallDir + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        List<string> files = SourceFiles(sourceDir);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(staging);
                foreach (string file in files)
                {
                    string target = Path.Combine(staging, Path.GetRelativePath(sourceDir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(1000);
            }
        }

        foreach (string file in files)
        {
            string copy = Path.Combine(staging, Path.GetRelativePath(sourceDir, file));
            if (!Hash(file).AsSpan().SequenceEqual(Hash(copy)))
                throw new InvalidOperationException($"La comprobación SHA-256 de {Path.GetFileName(file)} ha fallado.");
        }

        string backup = $"{InstallDir}.previous-{Guid.NewGuid():N}";
        bool hadPrevious = Directory.Exists(InstallDir);
        if (hadPrevious) Directory.Move(InstallDir, backup);
        try
        {
            Directory.Move(staging, InstallDir);
        }
        catch
        {
            if (hadPrevious) Directory.Move(backup, InstallDir);
            throw;
        }

        if (hadPrevious)
        {
            try { Directory.Delete(backup, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DeleteAtRestart(backup); }
        }
        Log.Info("Install", $"{files.Count} archivos copiados y comprobados en {InstallDir}");
    }

    /// Every file of the application folder, except settings, logs and caches of a portable run. A link or reparse
    /// point anywhere stops the installation: the folder is supposed to be a plain unzipped copy.
    private static List<string> SourceFiles(string sourceDir)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(sourceDir);
        while (pending.Count > 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"{entry} es un enlace; descomprime el ZIP en una carpeta normal.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                string name = Path.GetFileName(entry);
                if (name.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cache.json", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(entry);
            }
        }
        if (!files.Any(f => Path.GetFileName(f).Equals("OpenControlEdge.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("No se encuentra OpenControlEdge.exe en la carpeta de origen.");
        return files;
    }

    private static byte[] Hash(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    /// Protects the data folder and brings older settings over. A file with several hard links (widget.log once
    /// ended up shared with the Claude app's container) loses this name before the folder is protected — its data
    /// survives under the other name — so the elevated widget never writes through it.
    private static void PrepareDataFolder(byte[]? oldSettings, List<string> warnings)
    {
        string dir = DataFolder.Path;
        var info = new DirectoryInfo(dir);
        if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("La carpeta de datos es un enlace o punto de reanálisis; se cancela para evitar escrituras elevadas fuera de ella.");

        if (info.Exists)
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (LinkCount(file) <= 1) continue;
                File.Delete(file);
                warnings.Add($"{Path.GetFileName(file)} estaba enlazado con otro archivo y se ha empezado uno nuevo.");
            }
        }

        DataFolder.Harden();

        string settings = Path.Combine(dir, SettingsFileName);
        if (oldSettings is not null && !File.Exists(settings))
        {
            DataFolder.WriteAtomic(SettingsFileName, oldSettings);
            Log.Info("Install", "ajustes anteriores migrados");
        }
    }

    private static uint LinkCount(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info) ? info.NumberOfLinks : 1;
    }

    /// Marks a folder and everything in it for deletion at the next restart (files first, deepest folders first).
    internal static void DeleteAtRestart(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            MoveFileExW(file, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        foreach (string sub in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            MoveFileExW(sub, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        MoveFileExW(folder, null, MOVEFILE_DELAY_UNTIL_REBOOT);
    }
}
