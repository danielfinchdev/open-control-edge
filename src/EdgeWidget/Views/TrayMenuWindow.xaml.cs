using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static EdgeWidget.Interop.NativeMethods;

namespace EdgeWidget.Views;

/// Tray menu drawn entirely with WPF templates. Closes on deactivation, Escape, or any click outside it
/// (the outside-click watch covers the case where Windows refuses to give the menu foreground).
public partial class TrayMenuWindow : Window
{
    private const double ShadowMargin = 20;

    private readonly DispatcherTimer _outsideClickWatch;
    private bool _closing;

    internal event Action? RefreshRequested;
    internal event Action? ExitRequested;

    public TrayMenuWindow()
    {
        InitializeComponent();

        _outsideClickWatch = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        _outsideClickWatch.Tick += OnOutsideClickWatch;

        SourceInitialized += (_, _) =>
            AddExtendedStyle(new WindowInteropHelper(this).Handle, WS_EX_TOOLWINDOW, WS_EX_APPWINDOW);
        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseMenu();
        };
    }

    internal void ShowAtCursor()
    {
        GetCursorPos(out POINT cursor);

        Opacity = 0;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
            ? dpiX / 96.0
            : VisualTreeHelper.GetDpi(this).DpiScaleX;

        int width = (int)Math.Ceiling(ActualWidth * scale);
        int height = (int)Math.Ceiling(ActualHeight * scale);
        int margin = (int)Math.Round(ShadowMargin * scale);

        // Bottom-right corner of the visible menu at the cursor, kept inside the work area.
        int x = cursor.X - width + margin;
        int y = cursor.Y - height + margin;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            RECT work = info.rcWork;
            x = Math.Max(work.Left - margin, Math.Min(x, work.Right - width + margin));
            y = Math.Max(work.Top - margin, Math.Min(y, work.Bottom - height + margin));
        }
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE);

        var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);

        Activate();
        SetForegroundWindow(hwnd);
        _outsideClickWatch.Start();
    }

    internal void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        _outsideClickWatch.Stop();
        Close();
    }

    private void OnOutsideClickWatch(object? sender, EventArgs e)
    {
        if (!IsAnyMouseButtonDown() || !GetCursorPos(out POINT cursor)) return;
        Point local = MenuBody.PointFromScreen(new Point(cursor.X, cursor.Y));
        bool inside = local.X >= 0 && local.Y >= 0 && local.X < MenuBody.ActualWidth && local.Y < MenuBody.ActualHeight;
        if (!inside) CloseMenu();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        RefreshRequested?.Invoke();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        ExitRequested?.Invoke();
    }
}
