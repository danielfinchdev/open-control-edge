using System.Diagnostics;
using System.Runtime.InteropServices;
using static OpenControlEdge.Interop.ProcessNative;

namespace OpenControlEdge.Services;

/// Physical memory in bytes. Percent is the memory load Windows reports (GlobalMemoryStatusEx.dwMemoryLoad).
/// Cached: the system file cache (standby included). Committed / CommitLimit: committed memory and its limit.
internal sealed record RamSnapshot(double Percent, ulong UsedBytes, ulong TotalBytes, ulong? CachedBytes,
    ulong? CommittedBytes, ulong? CommitLimitBytes, string? Message)
{
    public static RamSnapshot Failed(string message) => new(0, 0, 0, null, null, null, message);
}

/// FreedBytes: how much the memory in use (what the ring shows) dropped right after the clean-up (never negative).
/// Trimmed: how many processes had their working set trimmed.
internal sealed record RamCleanResult(ulong FreedBytes, int Trimmed);

/// Physical memory reading and the one-off "Liberar RAM". Never throws.
internal static class MemoryService
{
    /// Never touched: this process, and the ones Windows needs to stay alive (also caught by IsProcessCritical).
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "LsaIso", "fontdrvhost", "dwm", "MsMpEng", "audiodg", "explorer",
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost",
    };

    public static RamSnapshot Read()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
                return RamSnapshot.Failed("No se pudo leer la memoria");

            ulong? cached = null, committed = null, commitLimit = null;
            if (GetPerformanceInfo(out PERFORMANCE_INFORMATION perf, Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            {
                ulong page = (ulong)perf.PageSize;
                cached = (ulong)perf.SystemCache * page;
                committed = (ulong)perf.CommitTotal * page;
                commitLimit = (ulong)perf.CommitLimit * page;
            }

            ulong used = status.ullTotalPhys - Math.Min(status.ullAvailPhys, status.ullTotalPhys);
            return new RamSnapshot(status.dwMemoryLoad, used, status.ullTotalPhys, cached, committed, commitLimit, null);
        }
        catch (Exception ex)
        {
            Log.Warn("RAM", ex.GetType().Name + ": " + ex.Message);
            return RamSnapshot.Failed("No se pudo leer la memoria");
        }
    }

    /// Trims the working set of every process of this session it may open (not this one, not the critical ones), so
    /// their idle pages leave physical memory. The standby list (the file cache) is never purged. Windows pages back
    /// in whatever is used next: a one-off clean-up, like the "clear memory" button of a phone, not a lasting saving.
    /// Runs on a worker thread.
    public static Task<RamCleanResult> CleanAsync() => Task.Run(Clean);

    private static RamCleanResult Clean()
    {
        ulong before = Available();
        int trimmed = 0;
        int self = Environment.ProcessId;
        int session = Process.GetCurrentProcess().SessionId;

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || process.Id <= 4 || process.SessionId != session || Excluded.Contains(process.ProcessName)
                        || process.ProcessName.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("vmwp", StringComparison.OrdinalIgnoreCase)) continue;
                    IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, (uint)process.Id);
                    if (handle == IntPtr.Zero) continue;  // other users' or protected processes: skipped
                    try
                    {
                        if (IsProcessCritical(handle, out bool critical) && critical) continue;
                        if (EmptyWorkingSet(handle)) trimmed++;
                    }
                    finally { CloseHandle(handle); }
                }
                catch (InvalidOperationException) { }  // exited meanwhile
            }
        }

        ulong after = Available();
        var result = new RamCleanResult(after > before ? after - before : 0, trimmed);
        Log.Info("RAM", $"liberar: {trimmed} procesos recortados, {result.FreedBytes / (1024 * 1024)} MB en uso menos");
        return result;
    }

    private static ulong Available()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : 0;
    }
}
