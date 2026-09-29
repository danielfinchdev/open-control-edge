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

/// Freed: how much the memory in use (what the ring shows) dropped right after the clean-up, in bytes (never negative).
/// CacheFreed: how much the system cache (standby list included) dropped. Partial: some step could not run (the
/// standby purge needs administrator rights).
internal sealed record RamCleanResult(ulong FreedBytes, ulong CacheFreedBytes, int Trimmed, bool StandbyPurged, bool Partial);

/// Physical memory reading and the one-off "Liberar RAM". Never throws.
internal static class MemoryService
{
    /// Never touched: this process, and the ones Windows needs to stay alive (also caught by IsProcessCritical).
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "LsaIso", "fontdrvhost", "dwm", "MsMpEng", "audiodg",
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

    /// Trims the working set of every process it may open (not this one, not the critical ones) and purges the
    /// standby list. Windows pages back in whatever is used next and refills the cache: a one-off clean-up, like the
    /// "clear memory" button of a phone. Runs on a worker thread.
    public static Task<RamCleanResult> CleanAsync() => Task.Run(Clean);

    private static RamCleanResult Clean()
    {
        ulong before = Available();
        ulong cacheBefore = Cache();
        int trimmed = 0;
        bool partial = false;
        int self = Environment.ProcessId;

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || process.Id <= 4 || Excluded.Contains(process.ProcessName)) continue;
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

        // Needs SeProfileSingleProcessPrivilege, which only an elevated administrator token holds.
        bool purged = false;
        if (EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            int command = MemoryPurgeStandbyList;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            purged = status == 0;
            if (!purged) Log.Warn("RAM", $"purga de la lista standby: NTSTATUS 0x{status:X8}");
        }
        else
        {
            partial = true;
        }

        ulong after = Available();
        ulong cacheAfter = Cache();
        var result = new RamCleanResult(after > before ? after - before : 0,
            cacheBefore > cacheAfter ? cacheBefore - cacheAfter : 0, trimmed, purged, partial);
        Log.Info("RAM", $"liberar: {trimmed} procesos recortados, standby {(purged ? "purgada" : "sin purgar")}, "
                        + $"{result.FreedBytes / (1024 * 1024)} MB en uso menos, caché {result.CacheFreedBytes / (1024 * 1024)} MB menos");
        return result;
    }

    private static ulong Available()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : 0;
    }

    private static ulong Cache() =>
        GetPerformanceInfo(out PERFORMANCE_INFORMATION perf, Marshal.SizeOf<PERFORMANCE_INFORMATION>())
            ? (ulong)perf.SystemCache * (ulong)perf.PageSize : 0;
}
