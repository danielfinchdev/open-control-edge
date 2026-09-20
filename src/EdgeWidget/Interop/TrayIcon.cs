using System.Runtime.InteropServices;
using System.Windows.Interop;
using EdgeWidget.Services;
using static EdgeWidget.Interop.NativeMethods;

namespace EdgeWidget.Interop;

/// Notification-area icon built directly on Shell_NotifyIcon (no WinForms).
/// The menu itself is a WPF window (TrayMenuWindow); this class only reports clicks.
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;

    private readonly HwndSource _host;
    private readonly uint _taskbarCreatedMessage;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private string _tooltip;
    private bool _added;

    /// Raised on left or right click.
    public event Action? Clicked;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;

        // Hidden top-level window (not message-only) so it also receives the TaskbarCreated broadcast.
        _host = new HwndSource(new HwndSourceParameters("EdgeWidgetTrayHost")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _host.AddHook(WndProc);

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // The app runs elevated; Explorer runs at medium integrity. Without these filters UIPI drops
        // Explorer's callback messages and the icon would never respond to clicks.
        ChangeWindowMessageFilterEx(_host.Handle, CallbackMessage, MSGFLT_ALLOW, IntPtr.Zero);
        if (_taskbarCreatedMessage != 0)
            ChangeWindowMessageFilterEx(_host.Handle, _taskbarCreatedMessage, MSGFLT_ALLOW, IntPtr.Zero);

        (_icon, _ownsIcon) = LoadApplicationIcon();
        Add();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (!_added) return;
        var data = CreateData(NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_added) Log.Info("Tray", "icon added");
        else Log.Warn("Tray", "Shell_NotifyIcon(NIM_ADD) failed");
    }

    private NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _host.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)CallbackMessage)
        {
            int mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage is WM_LBUTTONUP or WM_RBUTTONUP)
            {
                handled = true;
                try { Clicked?.Invoke(); }
                catch (Exception ex) { Log.Error("Tray", ex); }
            }
        }
        else if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // Explorer restarted: the icon is gone and must be added again.
            Add();
        }
        return IntPtr.Zero;
    }

    private static (IntPtr Icon, bool Owned) LoadApplicationIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var small = new IntPtr[1];
                if (ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                    return (small[0], true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray", ex);
        }
        return (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _host.RemoveHook(WndProc);
        _host.Dispose();
    }
}
