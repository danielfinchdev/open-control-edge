using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
///   2. copies OpenControlEdge.exe and its native libraries (NativeLibraries, nothing else) to
///      C:\Program Files\OpenControlEdge through a staging folder, checks every copy with SHA-256 and swaps it in (the
///      previous copy comes back if the swap fails), then checks that no non-administrator can write to the folder or
///      the executable;
///   3. protects the data folder %ProgramData%\OpenControlEdge\{SID} (DataFolder.Harden: no links, admins-only
///      writes, High label);
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
            PrepareDataFolder();

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

        // The exe is the one the user just launched and approved through UAC: its copy must match it byte for byte. The
        // libraries must be the ones this exe was published with (their SHA-256 is compiled into it), so a library
        // swapped in the unzipped folder never reaches Program Files. The SignPath signature is required for
        // downloaded updates (UpdateService).
        foreach (string file in files)
        {
            string copy = Path.Combine(staging, Path.GetRelativePath(sourceDir, file));
            bool intact = Path.GetFileName(file).Equals("OpenControlEdge.exe", StringComparison.OrdinalIgnoreCase)
                ? Hash(file).AsSpan().SequenceEqual(Hash(copy))
                : NativeLibraries.IsExpected(copy);
            if (!intact)
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

    /// OpenControlEdge.exe plus the closed list of native libraries published next to it (NativeLibraries): nothing
    /// else in the folder is copied, so settings, logs and caches of a portable run stay behind. A missing file, or a
    /// link or reparse point in place of one, stops the installation. The exe cannot start without those libraries:
    /// IncludeNativeLibrariesForSelfExtract is false, so they are never extracted to a user-writable folder.
    private static List<string> SourceFiles(string sourceDir)
    {
        string executable = Path.Combine(sourceDir, "OpenControlEdge.exe");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("No se encuentra OpenControlEdge.exe en la carpeta de origen.");
        var files = new List<string> { executable };
        foreach (string name in NativeLibraries.Names)
        {
            string library = Path.Combine(sourceDir, name);
            if (!File.Exists(library) || (File.GetAttributes(library) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Falta {name} junto a OpenControlEdge.exe. Descomprime el ZIP completo.");
            files.Add(library);
        }
        return files;
    }

    private static byte[] Hash(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    /// Protects the data folder. A link or reparse point in place of the folder, or a file inside it with several hard
    /// links (widget.log once ended up shared with the Claude app's container), stops the installation (DataFolder.Harden):
    /// the elevated widget must never write through one.
    private static void PrepareDataFolder()
    {
        string dir = DataFolder.Path;
        var info = new DirectoryInfo(dir);
        if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("La carpeta de datos es un enlace o punto de reanálisis; se cancela para evitar escrituras elevadas fuera de ella.");

        DataFolder.Harden();
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
