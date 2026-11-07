using System.Diagnostics;
using System.IO;

namespace OpenControlEdge.Services;

/// Stages verified releases beside the protected installation and swaps directories with rollback.
internal static class UpdateInstaller
{
    internal static async Task<string?> DownloadAndRestartAsync(UpdateRelease release, CancellationToken cancellationToken = default)
    {
        string stage = Installer.InstallDir + ".update-" + Guid.NewGuid().ToString("N");
        _activeStage = stage;
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
        // This helper runs from the payload, and the single-file host keeps its exe open for as long as it runs:
        // Windows refuses to rename a folder with an open file inside (ERROR_ACCESS_DENIED), so the payload itself can
        // never be moved into place. A copy beside it, inside the same admin-only stage, is what gets swapped in.
        string ready = Path.Combine(stage, "ready");
        try
        {
            bool hadInstall = Directory.Exists(install);
            if (!CopyPayload(fullPayload, ready) || DataFolder.NonAdminsCanWrite(ready, directory: true)
                || !SwapDirectory(ready, install, backup))
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

    /// Copies the staged files (flat: the exe and its native libraries, UpdateService.ApplicationEntries) into a fresh
    /// folder and checks every copy byte for byte. Reading works while the helper runs from the payload; renaming does
    /// not.
    internal static bool CopyPayload(string payload, string ready)
    {
        try
        {
            if (Directory.Exists(ready)) Directory.Delete(ready, recursive: true);
            if (Directory.EnumerateDirectories(payload).Any()) throw new IOException("unexpected folder in the staged payload");
            Directory.CreateDirectory(ready);
            foreach (string file in Directory.GetFiles(payload))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("link in the staged payload");
                string copy = Path.Combine(ready, Path.GetFileName(file));
                File.Copy(file, copy);
                if (NativeLibraries.HashHex(copy) != NativeLibraries.HashHex(file))
                    throw new IOException("copy of " + Path.GetFileName(file) + " does not match the staged file");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Updates", "could not copy the staged payload: " + Describe(ex));
            return false;
        }
    }

    /// Shared swap primitive so the local release fixture exercises replacement, rollback and a current directory
    /// inside the payload. The current directory is moved out of both folders first: Windows does not rename a
    /// folder that is a process's current directory. Every move is retried for about 10 s while a file inside is
    /// briefly held (antivirus scan, the just-exited app).
    internal static bool SwapDirectory(string payload, string target, string backup, bool failAfterBackup = false)
    {
        bool movedAside = false;
        try
        {
            Directory.SetCurrentDirectory(Environment.SystemDirectory);
            if (Directory.Exists(target))
            {
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                MoveWithRetry(target, backup);
                movedAside = true;
            }
            if (failAfterBackup || !Directory.Exists(payload)) throw new IOException("simulated or missing staged payload");
            MoveWithRetry(payload, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Updates", "swap failed: " + Describe(ex));
            if (movedAside && Directory.Exists(backup) && !Directory.Exists(target))
            {
                try
                {
                    MoveWithRetry(backup, target);
                    Log.Info("Updates", "rollback: previous installation restored");
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("Updates", "rollback failed: " + Describe(rollback));
                }
            }
            return false;
        }
    }

    /// Waits between attempts: 9.75 s in all.
    internal static readonly int[] MoveRetryDelaysMs = [250, 500, 1000, 2000, 3000, 3000];

    private static void MoveWithRetry(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < MoveRetryDelaysMs.Length && IsTransient(ex))
            {
                Log.Warn("Updates", $"move {Path.GetFileName(source)} -> {Path.GetFileName(destination)}, attempt {attempt + 1}: {Describe(ex)}");
                Thread.Sleep(MoveRetryDelaysMs[attempt]);
            }
        }
    }

    /// Access denied, sharing or lock violation: a handle open inside the folder. Not a missing folder or an existing
    /// destination, which no wait fixes.
    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is IOException and not (DirectoryNotFoundException or FileNotFoundException) && (ex.HResult & 0xFFFF) is 5 or 32 or 33;

    private static string Describe(Exception ex) => $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";

    private static volatile string? _activeStage;

    /// The installed elevated app, shortly after start: removes the staging and backup folders earlier updates left
    /// beside the installation (the helper cannot delete the stage it runs from). One still in use is kept for the
    /// next start.
    internal static void CleanLeftovers(string install)
    {
        string parent = Path.GetDirectoryName(install)!;
        string name = Path.GetFileName(install);
        foreach (string prefix in new[] { name + ".update-", name + ".previous-update-" })
        {
            foreach (string dir in Directory.EnumerateDirectories(parent, prefix + "*"))
            {
                string leaf = Path.GetFileName(dir);
                if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(leaf[prefix.Length..], "N", out _)
                    || string.Equals(dir, _activeStage, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // The updater never creates links here: one is left alone rather than deleted elevated.
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    {
                        Log.Warn("Updates", $"leftover {leaf} kept: it is a link");
                        continue;
                    }
                    Directory.Delete(dir, recursive: true);
                    Log.Info("Updates", "removed leftover " + leaf);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("Updates", $"leftover {leaf} kept: {Describe(ex)}");
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception ex) { Log.Warn("Updates", "staging cleanup deferred: " + Describe(ex)); }
    }
}
