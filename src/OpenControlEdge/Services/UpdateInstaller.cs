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
                WorkingDirectory = payload,
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

    /// Runs in the verified new binary. The parent exits before its installation folder is renamed.
    internal static bool ApplyAfterParentExit(string payload, int parentPid)
    {
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
            return false;
        }

        string backup = install + ".previous-update-" + Guid.NewGuid().ToString("N");
        try
        {
            if (parentPid > 0)
            {
                try
                {
                    using Process parent = Process.GetProcessById(parentPid);
                    if (!parent.WaitForExit(60_000)) throw new TimeoutException("La app anterior no se cerró.");
                }
                catch (ArgumentException) { }
            }

            bool hadInstall = Directory.Exists(install);
            if (!SwapDirectory(fullPayload, install, backup))
            {
                TryDelete(stage);
                return false;
            }

            string executable = Path.Combine(install, "OpenControlEdge.exe");
            try { Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = install })?.Dispose(); }
            catch (Exception ex) { Log.Warn("Updates apply", "no se pudo reiniciar la nueva versión: " + ex.GetType().Name); }
            if (hadInstall) Installer.DeleteAtRestart(backup);
            TryDelete(stage);
            Log.Info("Updates", "updated installation swapped and relaunched");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Updates apply", ex);
            TryDelete(stage);
            return false;
        }
    }

    /// Shared swap primitive so the local release fixture exercises both replacement and rollback behavior.
    internal static bool SwapDirectory(string payload, string target, string backup, bool failAfterBackup = false)
    {
        bool hadTarget = Directory.Exists(target);
        try
        {
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
