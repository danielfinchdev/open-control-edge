using System.Runtime.InteropServices;

namespace OpenControlEdge.Interop;

/// kernel32 / advapi32 / userenv / psapi / ntdll: tokens, processes, jobs and memory. Every handle is closed by the
/// caller; nothing here throws.
internal static class ProcessNative
{
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    public const uint TOKEN_ADJUST_SESSIONID = 0x0100;

    public const int TokenElevation = 20;
    public const int TokenIntegrityLevel = 25;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;

    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;

    public const int STARTF_USESHOWWINDOW = 0x00000001;
    public const short SW_HIDE = 0;

    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    public const int SystemMemoryListInformation = 80;
    public const int MemoryPurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel,
        int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? application, string commandLine,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? application, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION info);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, int size);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessCritical(IntPtr process, out bool critical);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int length,
        IntPtr previousState, IntPtr returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool MoveFileExW(string existing, string? replacement, uint flags);

    public const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    /// True when the token is elevated (a full administrator token). False on any failure.
    public static bool IsTokenElevated(IntPtr token) =>
        GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;

    /// This process runs with a full administrator token.
    public static bool IsCurrentProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return true;
        try
        {
            if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)) return true;
            return elevated != 0;
        }
        finally { CloseHandle(token); }
    }

    /// Enables one privilege the token already holds (for example SeProfileSingleProcessPrivilege). False when the
    /// token does not hold it — AdjustTokenPrivileges then succeeds but reports ERROR_NOT_ALL_ASSIGNED.
    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out LUID luid)) return false;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }
}
