using System.IO;
using System.Runtime.InteropServices;
using static OpenControlEdge.Interop.GameNative;

namespace OpenControlEdge.Services;

/// What the install window checks before installing ("Comprobando tu sistema…") and fixes on its own: the Secondary
/// Logon service (seclogon, needed to open anything as the plain user) and PawnIO (the driver the temperatures need).
internal static class SystemCheck
{
    internal enum State { Ready, WillFix, Missing, Info }

    internal sealed record Item(string Key, State State, string? Detail);

    /// Every check, read-only. Never throws.
    public static IReadOnlyList<Item> Run()
    {
        var items = new List<Item>
        {
            new("Windows", Environment.Is64BitOperatingSystem && Environment.OSVersion.Version.Build >= 17763 ? State.Ready : State.Missing,
                $"{(Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10")} · {Environment.OSVersion.Version.Build}"),
            new("Admin", UnelevatedLauncher.IsElevated ? State.Ready : State.Missing, null),
            new("Seclogon", SeclogonReady() ? State.Ready : State.WillFix, null),
            new("PawnIo", PawnIoInstalled() ? State.Ready : State.WillFix, null),
        };
        string[] agents = AiDetector.All.Where(AiDetector.IsInstalled).Select(id => id.ToString()).ToArray();
        items.Add(new("Agents", State.Info, agents.Length > 0 ? string.Join(", ", agents) : null));
        return items;
    }

    private static bool PawnIoInstalled()
    {
        try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
        catch { return false; }
    }

    // ───────────────────────────── Secondary Logon ─────────────────────────────

    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_DEMAND_START = 3;
    private const uint SERVICE_DISABLED = 4;

    /// Not disabled (it starts on demand when the widget opens something as the user).
    public static bool SeclogonReady() => SeclogonStartType() is uint start && start != SERVICE_DISABLED;

    /// Sets a disabled Secondary Logon back to manual start, as Windows ships it. Null on success, else why not.
    public static string? FixSeclogon()
    {
        if (SeclogonReady()) return null;
        IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) return "No se pudo abrir el administrador de servicios";
        try
        {
            IntPtr service = OpenServiceW(manager, "seclogon", SERVICE_CHANGE_CONFIG | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return "No se encuentra el servicio Inicio de sesión secundario";
            try
            {
                return ChangeServiceConfigW(service, SERVICE_NO_CHANGE, SERVICE_DEMAND_START, SERVICE_NO_CHANGE,
                    null, null, IntPtr.Zero, null, null, null, null)
                    ? null : $"No se pudo activar el servicio (error {Marshal.GetLastWin32Error()})";
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static uint? SeclogonStartType()
    {
        IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) return null;
        try
        {
            IntPtr service = OpenServiceW(manager, "seclogon", SERVICE_QUERY_CONFIG);
            if (service == IntPtr.Zero) return null;
            try
            {
                QueryServiceConfigW(service, IntPtr.Zero, 0, out uint needed);
                if (needed == 0 || needed > 64 * 1024) return null;
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    // QUERY_SERVICE_CONFIG: dwServiceType, then dwStartType.
                    return QueryServiceConfigW(service, buffer, needed, out _) ? (uint)Marshal.ReadInt32(buffer, 4) : null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfigW(IntPtr service, IntPtr config, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);
}
