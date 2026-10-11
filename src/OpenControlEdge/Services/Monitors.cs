using System.Runtime.InteropServices;
using static OpenControlEdge.Interop.NativeMethods;

namespace OpenControlEdge.Services;

/// One monitor as Windows reports it: its device name ("\\.\DISPLAY2", what the settings store), its whole area and its
/// work area (without the taskbar) in physical pixels of the virtual screen, its effective DPI and whether it is the
/// primary one. Number is its position in the list (1, 2…) for the settings.
internal sealed record MonitorInfo(string Device, RECT Bounds, RECT Work, int Dpi, bool Primary, int Number)
{
    public int Width => Bounds.Right - Bounds.Left;
    public int Height => Bounds.Bottom - Bounds.Top;
}

/// The monitors of this PC (EnumDisplayMonitors), for the panel's placement and the settings. Never throws.
internal static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var found = new List<(string Device, RECT Bounds, RECT Work, int Dpi, bool Primary)>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(monitor, ref info))
                    found.Add((info.szDevice ?? "", info.rcMonitor, info.rcWork, DpiOf(monitor), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Log.Warn("Monitors", ex.GetType().Name);
        }
        // Left to right, then top to bottom: the order people number their screens in.
        return [.. found.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .Select((m, i) => new MonitorInfo(m.Device, m.Bounds, m.Work, m.Dpi, m.Primary, i + 1))];
    }

    /// The monitor with this device name; the primary one when it is null or no longer connected.
    public static MonitorInfo? Find(string? device)
    {
        IReadOnlyList<MonitorInfo> all = All();
        return all.FirstOrDefault(m => device is not null && m.Device.Equals(device, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
    }

    /// The monitor under a point of the virtual screen (physical pixels), or the nearest one.
    public static MonitorInfo? At(int x, int y)
    {
        IntPtr handle = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info)) return null;
        return All().FirstOrDefault(m => m.Device.Equals(info.szDevice, StringComparison.OrdinalIgnoreCase));
    }

    private static int DpiOf(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? (int)dpiX : 96;
}
