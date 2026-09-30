using System.Diagnostics;
using System.IO;

namespace OpenControlEdge.Services;

/// Stages verified releases beside the protected installation and swaps directories with rollback.
internal static class UpdateInstaller
{
    internal static async Task<string?> DownloadAndRestartAsync(UpdateRelease release, CancellationToken cancellationToken = default)
    {
        string stage = Installer.InstallDir + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!Installer.IsInstalledCopy) return "Instala Open Control Edge antes de actualizar.";
            if (!UnelevatedLauncher.IsElevated) return "La actualización requiere iniciar la app instalada con permisos de administrador.";
            string programFiles = Path.GetDirectoryName(Installer.InstallDir)!;
            if (DataFolder.NonAdminsCanWrite(programFiles, directory: true) || DataFolder.NonAdminsCanWrite(Installer.InstallDir, directory: true))
                return "La carpeta de instalación no está protegida; se cancela la actualización.";
            string payload = await UpdateService.DownloadAndStageAsync(release, stage, cancellationToken);
            if (DataFolder.NonAdminsCanWrite(stage, directory: true) || DataFolder.NonAdminsCanWrite(payload, directory: true))
                return "No se pudo proteger staging; no se ejecutará la actualización.";

            string helper = Path.Combine(payload, "OpenControlEdge.exe");
            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = true,
                Verb = "runas",
                // Never the payload itself: Windows does not rename a folder that is a process's current directory,
                // and the helper renames this one into place.
                WorkingDirectory = Environment.SystemDirectory,
                Arguments = $"--apply-update \"{payload}\" {Environment.ProcessId}",
            };
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            TryDelete(stage);
            return "Se canceló el permiso para actualizar.";
        }
        catch (Exception ex)
        {
            Log.Warn("Updates", "stage/install: " + ex.GetType().Name + ": " + ex.Message);
            TryDelete(stage);
            return ex is InvalidDataException ? ex.Message : "No se pudo preparar la actualización; la instalación actual sigue intacta.";
        }
    }

    /// Runs in the verified new binary (--apply-update), elevated, once the old app has quit: swaps the staged payload
    /// in and starts the installed copy, the new one or — when anything fails — the previous one.
    internal static bool ApplyAfterParentExit(string payload, int parentPid)
    {
        // First the old app has to be gone: it holds the installed files, and whatever is started below needs its
        // single-instance lock.
        if (parentPid > 0)
        {
            try
            {
                using Process parent = Process.GetProcessById(parentPid);
                if (!parent.WaitForExit(60_000))
                {
                    Log.Warn("Updates", "la app anterior no se cerró en 60 s; no se actualiza");
                    return false;
                }
            }
            catch (ArgumentException) { }  // already gone
        }

        string install = Installer.InstallDir;
        string expectedPrefix = install + ".update-";
        string fullPayload = Path.GetFullPath(payload);
        string? stage = fullPayload;
        while (stage is not null && !Path.GetFileName(stage).StartsWith(Path.GetFileName(expectedPrefix), StringComparison.OrdinalIgnoreCase))
            stage = Directory.GetParent(stage)?.FullName;
        string expectedHelper = Path.Combine(fullPayload, "OpenControlEdge.exe");
        if (!UnelevatedLauncher.IsElevated
            || !string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), expectedHelper, StringComparison.OrdinalIgnoreCase)
            || stage is null || !stage.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !fullPayload.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || DataFolder.NonAdminsCanWrite(stage, directory: true)
            || !File.Exists(Path.Combine(fullPayload, "OpenControlEdge.exe")))
        {
            Log.Warn("Updates", "update helper rejected staging path or permissions");
            StartInstalled();
            return false;
        }

        string backup = install + ".previous-update-" + Guid.NewGuid().ToString("N");
        try
        {
            bool hadInstall = Directory.Exists(install);
            if (!SwapDirectory(fullPayload, install, backup))
            {
                Log.Warn("Updates", "no se pudo sustituir la instalación; se vuelve a abrir la versión anterior");
                StartInstalled();
                TryDelete(stage);
                return false;
            }

            StartInstalled();
            if (hadInstall) Installer.DeleteAtRestart(backup);
            TryDelete(stage);
            Log.Info("Updates", "updated installation swapped and relaunched");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Updates apply", ex);
            StartInstalled();
            TryDelete(stage);
            return false;
        }
    }

    /// Opens whatever is installed now (the new version, or the previous one after a failed swap), so the widget never
    /// stays closed after the app quit to be updated.
    private static void StartInstalled()
    {
        if (!File.Exists(Installer.InstalledExe)) return;
        try
        {
            Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Installer.InstallDir })?.Dispose();
        }
        catch (Exception ex) { Log.Warn("Updates apply", "no se pudo abrir la versión instalada: " + ex.GetType().Name); }
    }

    /// Shared swap primitive so the local release fixture exercises replacement, rollback and a current directory
    /// inside the payload. The current directory is moved out of both folders first: Windows does not rename a
    /// folder that is a process's current directory.
    internal static bool SwapDirectory(string payload, string target, string backup, bool failAfterBackup = false)
    {
        bool hadTarget = Directory.Exists(target);
        try
        {
            Directory.SetCurrentDirectory(Environment.SystemDirectory);
            if (hadTarget)
            {
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                Directory.Move(target, backup);
            }
            if (failAfterBackup || !Directory.Exists(payload)) throw new IOException("simulated or missing staged payload");
            Directory.Move(payload, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (hadTarget && Directory.Exists(backup) && !Directory.Exists(target))
            {
                try { Directory.Move(backup, target); }
                catch (Exception rollback) { Log.Error("Updates rollback", rollback); }
            }
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception ex) { Log.Warn("Updates", "staging cleanup deferred: " + ex.GetType().Name); }
    }
}
