using System.Diagnostics;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shell;
using static OpenControlEdge.Interop.NativeMethods;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;
using System.Windows.Automation;
using IOPath = System.IO.Path;

namespace OpenControlEdge.Views;

/// A lightweight, on-demand settings window. Its controls are created only while it is open.
/// Look after shadcn/ui (zinc): the "Set.*" colours of Controls.xaml, swapped here for the light theme.
internal sealed class SettingsWindow : Window
{
    private readonly Func<Task> _refresh;
    private readonly Action<Settings> _apply;
    private readonly Func<AiProviderId, AgentStatus> _agentStatus;
    private readonly Func<AiProviderId, Task> _retry;
    private readonly Func<DateTimeOffset?> _lastRefresh;
    private readonly Func<bool> _sensorsAvailable;
    private readonly StackPanel _content = new();
    private readonly StackPanel _navigation = new();
    private readonly TextBlock _title = new();
    private readonly ScrollViewer _scroll;
    private readonly ScaleTransform _zoom = new(1, 1);
    private string _category = "General";
    private UpdateRelease? _release;
    private bool _noUpdateAvailable;
    private AiProviderId? _keyEditor;
    private AiProviderId? _accountEditor;
    private WidgetView? _editingView;
    private static readonly string[] Categories = ["General", "Personalización", "Agentes", "Modo juego", "Información", "Actualizaciones", "Feedback"];

    internal SettingsWindow(Func<Task> refresh, Action<Settings> apply, Func<AiProviderId, AgentStatus> agentStatus,
        Func<AiProviderId, Task> retry, string categoryName = "General", UpdateRelease? release = null,
        Func<DateTimeOffset?>? lastRefresh = null, Func<bool>? sensorsAvailable = null)
    {
        _refresh = refresh;
        _apply = apply;
        _agentStatus = agentStatus;
        _retry = retry;
        _lastRefresh = lastRefresh ?? (() => null);
        _sensorsAvailable = sensorsAvailable ?? (() => false);
        _category = categoryName;
        _release = release;
        Width = DesignWidth; Height = DesignHeight; MinWidth = MinDesignWidth; MinHeight = MinDesignHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; AllowsTransparency = true;
        // Resize borders and corners without a system frame; the header drags the window itself (DragMove).
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        // Maximised, a frameless transparent window spills past the work area, so it always stays a normal window.
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };
        // A normal window, never Topmost and without Owner (a window owned by the topmost panel stays above every
        // other window): it has a taskbar button and an Alt+Tab entry like any other.
        Background = Brushes.Transparent; ShowInTaskbar = true; Topmost = false; UseLayoutRounding = true;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        SetResourceReference(ForegroundProperty, "Set.Foreground");
        UsePalette();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var frame = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = .32 } };
        frame.SetResourceReference(Border.BorderBrushProperty, "Set.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Set.Background");
        var root = new DockPanel { LayoutTransform = _zoom };

        var header = new DockPanel { Margin = new Thickness(20, 12, 12, 12), Background = Brushes.Transparent };
        var close = new Button { Style = (Style)Application.Current.FindResource("PanelRoundButton"), Width = 30, Height = 30,
            Content = Glyph(Icons.Close, 14), VerticalAlignment = VerticalAlignment.Center };
        close.SetResourceReference(ToolTipProperty, "Settings.Close");
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        _title.FontSize = 15; _title.FontWeight = FontWeights.SemiBold; _title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_title);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var rule = new Border { Height = 1 }; rule.SetResourceReference(Border.BackgroundProperty, "Set.Border");
        DockPanel.SetDock(rule, Dock.Top); root.Children.Add(rule);

        _navigation.Width = 172; _navigation.Margin = new Thickness(12, 14, 0, 14);
        foreach (string category in Categories)
        {
            var b = new Button { Tag = category, Style = (Style)Application.Current.FindResource("Oce.NavItem") };
            b.Click += (_, _) => { _category = category; RenderPage(); };
            _navigation.Children.Add(b);
        }
        DockPanel.SetDock(_navigation, Dock.Left); root.Children.Add(_navigation);
        _content.Margin = new Thickness(0, 0, 12, 0);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20, 16, 8, 14), Content = _content };
        root.Children.Add(_scroll); frame.Child = root; Content = frame;
        ThemeManager.Changed += OnThemeChanged; Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        Loc.Changed += OnLanguageChanged; Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        SourceInitialized += (_, _) => Place();
        Closing += (_, _) => SaveBounds();
        RenderPage();
    }

    /// The panel: the window opens centred on its monitor. Not an Owner (see above).
    internal Window? Anchor { get; init; }

    /// Snapshots: never placed on screen, never reads or saves the window position.
    internal bool Preview { get; init; }

    // Size at zoom 1, in design units. DesignHeight fits the tallest page (General of the installed copy, Updates
    // with release notes); Snapshot fails if any page needs to scroll at the default size.
    private const double DesignWidth = 820;
    private const double DesignHeight = 780;
    private const double MinDesignWidth = 600;
    private const double MinDesignHeight = 420;
    // Zoom like the panel's automatic scale: 1 on a 1080p work area, larger on taller screens; smaller only when the
    // default size would not fit the work area (down to MinZoom, then the page scrolls).
    private const double ReferenceWorkHeight = 1040;
    private const double MinZoom = 0.8;
    private const double MaxZoom = 1.4;
    private const double ScreenMargin = 16;

    private static double ZoomFor(double workWidthDip, double workHeightDip)
    {
        double preferred = Math.Min(Math.Max(1, workHeightDip / ReferenceWorkHeight), MaxZoom);
        double fit = Math.Min((workWidthDip - 2 * ScreenMargin) / DesignWidth, (workHeightDip - 2 * ScreenMargin) / DesignHeight);
        return Math.Round(Math.Max(MinZoom, Math.Min(preferred, fit)), 3);
    }

    private void ApplyZoom(double zoom)
    {
        _zoom.ScaleX = _zoom.ScaleY = zoom;
        MinWidth = MinDesignWidth * zoom;
        MinHeight = MinDesignHeight * zoom;
    }

    /// Snapshots: the default size and zoom on a work area of the given size (DIPs).
    internal void PreviewWorkArea(double workWidthDip, double workHeightDip)
    {
        double zoom = ZoomFor(workWidthDip, workHeightDip);
        ApplyZoom(zoom);
        Width = DesignWidth * zoom;
        Height = DesignHeight * zoom;
    }

    /// Snapshots: a window resized by hand to the given size (DIPs), keeping the zoom.
    internal void PreviewSize(double width, double height)
    {
        Width = Math.Max(width, MinWidth);
        Height = Math.Max(height, MinHeight);
    }

    internal double Zoom => _zoom.ScaleX;

    /// Brings the window to the front (it is not topmost, so another window may be covering it).
    internal void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// Before the window is first shown: where it was last closed if that is still wholly inside the work area of a
    /// monitor with the same DPI; otherwise the default size, centred on the panel's monitor. In physical pixels.
    private void Place()
    {
        if (Preview) return;
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (SettingsStore.Load().SettingsWindow is WindowBounds saved && SavedPlacement(saved) is double savedZoom)
            {
                ApplyZoom(savedZoom);
                MoveTo(hwnd, saved.X, saved.Y, saved.Width, saved.Height);
                return;
            }
            IntPtr anchor = Anchor is null ? IntPtr.Zero : new WindowInteropHelper(Anchor).Handle;
            IntPtr monitor = anchor != IntPtr.Zero ? MonitorFromWindow(anchor, MONITOR_DEFAULTTONEAREST)
                : MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
            double dpi = MonitorDpi(monitor) / 96.0;
            RECT work = info.rcWork;
            double zoom = ZoomFor((work.Right - work.Left) / dpi, (work.Bottom - work.Top) / dpi);
            ApplyZoom(zoom);
            int width = (int)Math.Round(DesignWidth * zoom * dpi), height = (int)Math.Round(DesignHeight * zoom * dpi);
            MoveTo(hwnd, (work.Left + work.Right - width) / 2, (work.Top + work.Bottom - height) / 2, width, height);
        }
        catch (Exception ex) { Log.Warn("Settings", "window placement: " + ex.GetType().Name); }
    }

    /// The zoom for a saved rectangle, or null when it no longer fits a monitor as it was.
    private static double? SavedPlacement(WindowBounds saved)
    {
        var rect = new RECT { Left = saved.X, Top = saved.Y, Right = saved.X + saved.Width, Bottom = saved.Y + saved.Height };
        IntPtr monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || MonitorDpi(monitor) != saved.Dpi) return null;
        RECT work = info.rcWork;
        if (rect.Left < work.Left || rect.Top < work.Top || rect.Right > work.Right || rect.Bottom > work.Bottom) return null;
        double dpi = saved.Dpi / 96.0;
        double zoom = ZoomFor((work.Right - work.Left) / dpi, (work.Bottom - work.Top) / dpi);
        bool tooSmall = saved.Width < Math.Floor(MinDesignWidth * zoom * dpi) || saved.Height < Math.Floor(MinDesignHeight * zoom * dpi);
        return tooSmall ? null : zoom;
    }

    private static int MonitorDpi(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? (int)dpiX : 96;

    /// Onto the target monitor first (if its DPI differs WPF rescales the window there), then the exact size.
    private static void MoveTo(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// Closed while minimised, the position is not saved and the previous one is kept.
    private void SaveBounds()
    {
        if (Preview || WindowState != WindowState.Normal) return;
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return;
            var bounds = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, (int)GetDpiForWindow(hwnd));
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Dpi <= 0) return;
            if (SettingsStore.Load().SettingsWindow != bounds) SettingsStore.Update(x => x with { SettingsWindow = bounds });
        }
        catch (Exception ex) { Log.Warn("Settings", "window position: " + ex.GetType().Name); }
    }

    /// shadcn zinc: the dark set lives in Controls.xaml; the light one overrides it in this window only.
    private void UsePalette()
    {
        (string Key, uint Light)[] light =
        [
            ("Set.Background", 0xFFFFFFFF), ("Set.Card", 0xFFFFFFFF), ("Set.Border", 0xFFE4E4E7), ("Set.Input", 0xFFE4E4E7),
            ("Set.Ring", 0xFFA1A1AA), ("Set.Foreground", 0xFF09090B), ("Set.Muted", 0xFF71717A), ("Set.Secondary", 0xFFF4F4F5),
            ("Set.SecondaryHover", 0xFFE4E4E7), ("Set.Primary", 0xFF18181B), ("Set.PrimaryForeground", 0xFFFAFAFA),
            ("Set.PrimaryHover", 0xFF3F3F46),
        ];
        foreach ((string key, uint argb) in light)
        {
            if (ThemeManager.IsLight)
            {
                var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
                brush.Freeze();
                Resources[key] = brush;
            }
            else Resources.Remove(key);
        }
    }

    private void OnThemeChanged() { Dispatcher.InvokeAsync(() => { Background = Brushes.Transparent; UsePalette(); RenderPage(); }); }
    private void OnLanguageChanged() => Dispatcher.InvokeAsync(RenderPage);
    /// Only the Agents page shows agent state; any other page is left alone (rebuilding it would reset its controls).
    internal void RefreshAgents() => Dispatcher.InvokeAsync(() => { if (_category == "Agentes") RenderPage(); });
    /// The automatic check found a new version while this window is open: the banner appears on the current page.
    internal void ShowUpdateBanner(UpdateRelease release)
    {
        _release = release;
        _noUpdateAvailable = false;
        RenderPage();
    }

    /// "Nueva versión disponible": one click downloads, verifies and installs it, then the widget restarts.
    private void AddUpdateBanner(UpdateRelease release)
    {
        var banner = new Border { Style = StyleOf("Oce.Card"), Margin = new Thickness(0, 0, 0, 16), BorderThickness = new Thickness(1) };
        banner.SetResourceReference(Border.BorderBrushProperty, "Oce.Success");
        var panel = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = (Brush)Application.Current.FindResource("Oce.Success"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
        title.Children.Add(new TextBlock { Text = Loc.Format("Settings.UpdateBanner", release.Tag), FontWeight = FontWeights.SemiBold, FontSize = 14 });
        panel.Children.Add(title);
        var status = Muted(Loc.Get("Settings.UpdateBannerHint"), new Thickness(0, 4, 0, 0), 12.5);
        panel.Children.Add(status);
        banner.Child = panel;
        _content.Children.Add(banner);
        Actions(panel,
            Button(Loc.Get("Settings.UpdateDownload"), async (sender, _) => await InstallUpdateAsync(release, status, (Button)sender), true),
            Button(Loc.Get("Settings.UpdateNotes"), (_, _) => { _category = "Actualizaciones"; RenderPage(); }, variant: "Ghost"));
    }

    /// Downloads, verifies and stages the release, then hands over to the new executable (the widget restarts).
    private async Task InstallUpdateAsync(UpdateRelease release, TextBlock status, Button? button = null)
    {
        if (button is not null) button.IsEnabled = false;
        status.Text = Loc.Get("Settings.Downloading");
        string? error = await UpdateInstaller.DownloadAndRestartAsync(release);
        if (error is null) { Application.Current.Shutdown(); return; }
        status.Text = DisplayError(error);
        if (button is not null) button.IsEnabled = true;
    }

    internal void ShowUpdateResult(UpdateRelease release)
    {
        _release = release;
        _noUpdateAvailable = false;
        _category = "Actualizaciones";
        RenderPage();
        BringToFront();
    }
    private static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);
    /// Dictionary suffix of a category (Settings.Nav.* and Settings.Hint.*); the Spanish names are only identifiers.
    private static string CategoryKey(string c) => c switch
    {
        "Personalización" => "Appearance", "Agentes" => "Agents", "Modo juego" => "GameMode", "Información" => "About",
        "Actualizaciones" => "Updates", "Feedback" => "Feedback", _ => "General",
    };
    private static string Category(string c) => Loc.Get("Settings.Nav." + CategoryKey(c));
    private static string CategoryHint(string c) => Loc.Get("Settings.Hint." + CategoryKey(c));
    private static Drawing CategoryIcon(string c) => c switch
    {
        "General" => Icons.Gear, "Personalización" => Icons.Swatch, "Agentes" => Icons.Bot, "Modo juego" => Icons.Gamepad,
        "Información" => Icons.Info, "Actualizaciones" => Icons.Refresh, _ => Icons.Message,
    };
    private static Drawing? ProviderLogo(AiProviderId id) => id switch
    {
        AiProviderId.Claude => Icons.ClaudeSpark, AiProviderId.Codex => Icons.Codex, AiProviderId.Cursor => Icons.Cursor,
        AiProviderId.OpenCode => Icons.OpenCode, AiProviderId.DeepSeek => Icons.DeepSeek, AiProviderId.OpenRouter => Icons.OpenRouter,
        _ => null,
    };

    /// A monochrome icon in the text colour (or another "Set.*" colour).
    private static IconView Glyph(Drawing? icon, double size, string colour = "Set.Foreground")
    {
        var view = new IconView { Icon = icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        view.SetResourceReference(IconView.ForegroundProperty, colour);
        return view;
    }

    private void RenderPage()
    {
        _title.Text = Loc.Get("Settings.Title");
        Title = _title.Text;
        foreach (Button button in _navigation.Children)
        {
            string category = (string)button.Tag;
            bool active = category == _category;
            if (active) button.SetResourceReference(BackgroundProperty, "Set.Secondary"); else button.ClearValue(BackgroundProperty);
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(Glyph(CategoryIcon(category), 16, active ? "Set.Foreground" : "Set.Muted"));
            var text = new TextBlock { Text = Category(category), Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center,
                FontWeight = active ? FontWeights.Medium : FontWeights.Normal };
            text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Set.Foreground" : "Set.Muted");
            item.Children.Add(text);
            button.Content = item;
        }
        _content.Children.Clear();
        if (_release is not null && _category != "Actualizaciones") AddUpdateBanner(_release);
        AddHeading(Category(_category), CategoryHint(_category));
        switch (_category) { case "General": General(); break; case "Personalización": Appearance(); break; case "Agentes": Agents(); break; case "Modo juego": GameMode(); break; case "Información": About(); break; case "Actualizaciones": Updates(); break; case "Feedback": Feedback(); break; }
    }
    private void AddHeading(string text, string hint)
    {
        _content.Children.Add(new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _content.Children.Add(Muted(hint, new Thickness(0, 0, 0, 18)));
    }
    private static TextBlock Muted(string text, Thickness margin, double size = 13)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Set.Muted");
        return block;
    }
    private Border Card(string title, string description = "")
    {
        var panel = new StackPanel();
        if (title.Length > 0) panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        if (description.Length > 0) panel.Children.Add(Muted(description, new Thickness(0, 4, 0, 0)));
        if (title.Length > 0 || description.Length > 0) panel.Children.Add(new Border { Height = 10 });
        var card = new Border { Style = StyleOf("Oce.Card"), Child = panel }; _content.Children.Add(card); return card;
    }
    private static StackPanel Inside(Border card) => (StackPanel)card.Child;
    private static Button Button(string text, RoutedEventHandler click, bool primary = false, string? variant = null)
    {
        string styleKey = variant is not null ? "Oce.Button." + variant
            : text.Contains("Desinstalar", StringComparison.OrdinalIgnoreCase) || text.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
                ? "Oce.Button.Destructive" : primary ? "Oce.Button.Primary" : "Oce.Button.Outline";
        var b = new Button { Content = text, Style = StyleOf(styleKey) };
        b.Click += click; return b;
    }
    /// A row of buttons under the fields of a card.
    private static WrapPanel Actions(Panel panel, params Button[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (Button b in buttons) { b.Margin = new Thickness(0, 0, 8, 6); row.Children.Add(b); }
        panel.Children.Add(row); return row;
    }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0) };
    /// Setting row: label (and an optional hint underneath) on the left, the control on the right.
    private static FlowRow Row(Panel panel, string label, UIElement control, string? hint = null)
    {
        var row = new FlowRow { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(control);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(label));
        if (hint is not null) text.Children.Add(Muted(hint, new Thickness(0, 2, 16, 0), 12));
        row.Children.Add(text); panel.Children.Add(row); return row;
    }

    /// Children[0] (the control) to the right of Children[1] (its text), both centred vertically; when the text
    /// would be left with less than MinTextWidth, the control goes under the text instead, aligned to the left.
    private sealed class FlowRow : Panel
    {
        internal double MinTextWidth { get; init; } = 200;
        private const double StackGap = 8;
        private bool _stacked;

        protected override Size MeasureOverride(Size available)
        {
            UIElement control = InternalChildren[0], text = InternalChildren[1];
            control.Measure(new Size(double.PositiveInfinity, available.Height));
            double controlWidth = control.DesiredSize.Width;
            _stacked = !double.IsInfinity(available.Width) && available.Width - controlWidth < MinTextWidth;
            if (!_stacked)
            {
                text.Measure(new Size(Math.Max(0, available.Width - controlWidth), available.Height));
                return new Size(text.DesiredSize.Width + controlWidth, Math.Max(text.DesiredSize.Height, control.DesiredSize.Height));
            }
            text.Measure(new Size(available.Width, double.PositiveInfinity));
            control.Measure(new Size(available.Width, double.PositiveInfinity));
            return new Size(Math.Max(text.DesiredSize.Width, control.DesiredSize.Width),
                text.DesiredSize.Height + StackGap + control.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            UIElement control = InternalChildren[0], text = InternalChildren[1];
            if (!_stacked)
            {
                Size c = control.DesiredSize;
                double textWidth = Math.Max(0, final.Width - c.Width);
                text.Arrange(new Rect(0, (final.Height - text.DesiredSize.Height) / 2, textWidth, text.DesiredSize.Height));
                control.Arrange(new Rect(textWidth, (final.Height - c.Height) / 2, c.Width, c.Height));
                return final;
            }
            text.Arrange(new Rect(0, 0, final.Width, text.DesiredSize.Height));
            control.Arrange(new Rect(0, text.DesiredSize.Height + StackGap, Math.Min(control.DesiredSize.Width, final.Width), control.DesiredSize.Height));
            return final;
        }
    }
    private void General()
    {
        Settings s = SettingsStore.Load(); var card = Card(Loc.Get("Settings.Preferences")); var p = Inside(card);
        if (Installer.IsInstalledCopy || Preview)
            AddStartupToggle(p);
        AddChoice(p, Loc.Get("Settings.PanelMode"), [Loc.Get("Settings.Pinned"), Loc.Get("Settings.Automatic")], s.PanelMode == PanelMode.Auto ? 1 : 0, i => { var updated = SettingsStore.Update(x => x with { PanelMode = i == 0 ? PanelMode.Pinned : PanelMode.Auto }); if (updated != null) _apply(updated); });
        int[] intervals = [2, 5, 10, 15];
        AddChoice(p, Loc.Get("Settings.RefreshInterval"), intervals.Select(i => $"{i} min").ToArray(),
            Array.IndexOf(intervals, s.UsageRefreshMinutes), i => { var updated = SettingsStore.Update(x => x with { UsageRefreshMinutes = intervals[i] }); if (updated != null) _apply(updated); });
        var lastValue = _lastRefresh();
        var last = Muted(Loc.Format("Settings.LastUpdated", lastValue?.ToLocalTime().ToString("t", Loc.Culture) ?? "—"), new Thickness(0, 6, 0, 0));
        p.Children.Add(last);
        Actions(p, Button(Loc.Get("Settings.RefreshNow"), async (_, _) =>
        {
            last.Text = Loc.Get("Settings.Refreshing");
            await _refresh();
            last.Text = Loc.Format("Settings.LastUpdated", _lastRefresh()?.ToLocalTime().ToString("t", Loc.Culture) ?? DateTime.Now.ToString("t", Loc.Culture));
        }, true));

        var extra = Card(Loc.Get("Settings.Options")); var ep = Inside(extra);
        AddToggle(ep, Loc.Get("Settings.AutoRenewClaude"), s.AutoRenewClaude,
            (x, v) => x with { AutoRenewClaude = v }, x => x.AutoRenewClaude);
        AddToggle(ep, Loc.Get("Settings.RamCleanup"), s.RamCleanup,
            (x, v) => x with { RamCleanup = v }, x => x.RamCleanup);
        Actions(ep, Button(Loc.Get("Settings.Reset"), (_, _) =>
        {
            if (MessageBox.Show(Loc.Get("Settings.ResetConfirm"), "Open Control Edge",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Settings? reset = SettingsStore.Update(_ => Settings.Defaults);
            if (reset is null) return;
            ThemeManager.Apply(reset.Theme, reset.ColorTheme); Loc.Apply(reset.Language); _apply(reset); RenderPage();
        }, variant: "Secondary"));

        var danger = Inside(Card(Loc.Get("Settings.Uninstall"), Loc.Get("Settings.UninstallHint")));
        danger.Children.RemoveAt(danger.Children.Count - 1);
        Actions(danger, Button(Loc.Get("Settings.UninstallButton"), (_, _) =>
        {
            if (MessageBox.Show(Loc.Get("Settings.UninstallConfirm"), "Open Control Edge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var uninstall = new InstallWindow(InstallWindow.Mode.Uninstall) { Owner = this };
            uninstall.ShowDialog();
            if (uninstall.Result == InstallWindow.Outcome.Uninstalled) Application.Current.Shutdown();
        }));
    }
    private void Appearance()
    {
        Settings s = SettingsStore.Load(); var p = Inside(Card(Loc.Get("Settings.Look")));
        AddChoice(p, Loc.Get("Settings.Language"), ["Español", "English"], s.Language == UiLanguage.English ? 1 : 0, i => { var n = SettingsStore.Update(x => x with { Language = i == 0 ? UiLanguage.Spanish : UiLanguage.English }); if (n != null) Loc.Apply(n.Language); });
        AddChoice(p, Loc.Get("Settings.Theme"), [Loc.Get("Settings.Theme.Dark"), Loc.Get("Settings.Theme.Light"), Loc.Get("Settings.Theme.System")], (int)s.Theme, i => { var n = SettingsStore.Update(x => x with { Theme = (AppTheme)i }); if (n != null) ThemeManager.Apply(n.Theme, n.ColorTheme); });
        AddViews(s);
        AddRingThemes(s);
        AddBackground(s);
        AddScale(s);
    }

    private static readonly WidgetView[] AllViews = [WidgetView.Ai, WidgetView.Pc, WidgetView.Custom];

    private static Drawing? RingIcon(string key) => key switch
    {
        RingKeys.Claude => Icons.ClaudeSpark, RingKeys.Codex => Icons.Codex, RingKeys.Cursor => Icons.Cursor,
        RingKeys.OpenCode => Icons.OpenCode, RingKeys.DeepSeek => Icons.DeepSeek, RingKeys.OpenRouter => Icons.OpenRouter,
        RingKeys.Cpu => Icons.Cpu, RingKeys.Gpu => Icons.Gpu, RingKeys.Ram => Icons.Ram, RingKeys.Fps => Icons.Fps,
        RingKeys.GameMode => Icons.Gamepad, _ => null,
    };

    /// The view the panel shows, and for each view which rings it has and in what order (↑ ↓ and a switch per ring).
    private void AddViews(Settings s)
    {
        var p = Inside(Card(Loc.Get("Settings.Views"), Loc.Get("Settings.ViewsHint")));
        AddChoice(p, Loc.Get("Settings.ActiveView"), AllViews.Select(v => Loc.Get("View." + v)).ToArray(), Array.IndexOf(AllViews, s.View), i =>
        {
            Settings? updated = SettingsStore.Update(x => x with { View = AllViews[i] });
            if (updated is not null) _apply(updated);
        });

        WidgetView editing = _editingView ?? s.View;
        p.Children.Add(FieldLabel(Loc.Get("Settings.RingsOfView")));
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (WidgetView view in AllViews)
        {
            Button tab = Button(Loc.Get("View." + view), (_, _) => { _editingView = view; RenderPage(); }, variant: view == editing ? "Secondary" : "Ghost");
            tab.Margin = new Thickness(0, 0, 6, 0);
            tabs.Children.Add(tab);
        }
        p.Children.Add(tabs);

        ViewLayout layout = s.Layout(editing);
        for (int i = 0; i < layout.Order.Length; i++)
        {
            string key = layout.Order[i];
            int position = i;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button up = IconButton(Icons.ArrowUp, Loc.Get("Settings.MoveUp"), () => MoveRing(editing, position, -1));
            Button down = IconButton(Icons.ArrowDown, Loc.Get("Settings.MoveDown"), () => MoveRing(editing, position, 1));
            up.IsEnabled = position > 0;
            down.IsEnabled = position < layout.Order.Length - 1;
            controls.Children.Add(up);
            controls.Children.Add(down);
            ToggleButton show = Switch(!layout.Hidden.Contains(key), on =>
            {
                Settings? updated = SettingsStore.Update(x =>
                {
                    ViewLayout current = x.Layout(editing);
                    ImmutableHashSet<string> hidden = on ? current.Hidden.Remove(key) : current.Hidden.Add(key);
                    return x.WithLayout(editing, current with { Hidden = hidden });
                });
                if (updated is null) return !on;
                _apply(updated);
                return !updated.Layout(editing).Hidden.Contains(key);
            });
            show.Margin = new Thickness(10, 0, 0, 0);
            show.ToolTip = Loc.Get("Settings.ShowInPanel");
            controls.Children.Add(show);
            DockPanel.SetDock(controls, Dock.Right);
            row.Children.Add(controls);
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Glyph(RingIcon(key), 16));
            name.Children.Add(new TextBlock { Text = Loc.Get("RingName." + key), Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(name);
            p.Children.Add(row);
        }
        Actions(p, Button(Loc.Get("Settings.ResetView"), (_, _) =>
        {
            Settings? updated = SettingsStore.Update(x => x.WithLayout(editing, ViewLayout.Default(editing)));
            if (updated is not null) { _apply(updated); RenderPage(); }
        }, variant: "Secondary"));
    }

    private void MoveRing(WidgetView view, int position, int delta)
    {
        Settings? updated = SettingsStore.Update(x =>
        {
            ViewLayout current = x.Layout(view);
            int target = position + delta;
            if (target < 0 || target >= current.Order.Length) return x;
            var order = current.Order.ToList();
            (order[position], order[target]) = (order[target], order[position]);
            return x.WithLayout(view, current with { Order = [.. order] });
        });
        if (updated is null) return;
        _apply(updated);
        RenderPage();
    }

    private static Button IconButton(Drawing icon, string tip, Action click)
    {
        var b = new Button { Style = StyleOf("Oce.Button.Ghost"), Content = Glyph(icon, 14), ToolTip = tip, Padding = new Thickness(6),
            MinWidth = 0, Width = 30, Height = 30 };
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// Panel background: presets, a #RRGGBB field and red / green / blue sliders, applied live and saved after a pause.
    private void AddBackground(Settings s)
    {
        var p = Inside(Card(Loc.Get("Settings.Background"), Loc.Get("Settings.BackgroundHint")));
        Color initial = s.PanelBackground ?? ThemeManager.ColorOf(ThemeManager.Background);
        var swatch = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(initial), VerticalAlignment = VerticalAlignment.Center };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Set.Border");
        var hex = new TextBox { Style = StyleOf("Oce.Input"), Width = 110, Text = SettingsStore.ColorToHex(initial), Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, MaxLength = 7 };
        AutomationProperties.SetName(hex, Loc.Get("Settings.BackgroundHex"));
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(swatch);
        head.Children.Add(hex);
        p.Children.Add(head);

        var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        Color? pending = s.PanelBackground;
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            Settings? updated = SettingsStore.Update(x => x with { PanelBackground = pending });
            if (updated is not null) _apply(updated);
        };
        Closed += (_, _) => { if (saveTimer.IsEnabled) { saveTimer.Stop(); SettingsStore.Update(x => x with { PanelBackground = pending }); } };

        var sliders = new Slider[3];
        bool syncing = false;
        void Use(Color? color, bool fromSliders = false, bool fromText = false)
        {
            pending = color;
            Color shown = color ?? DefaultBackground();
            swatch.Background = new SolidColorBrush(shown);
            syncing = true;
            if (!fromText) hex.Text = SettingsStore.ColorToHex(shown);
            if (!fromSliders) { sliders[0].Value = shown.R; sliders[1].Value = shown.G; sliders[2].Value = shown.B; }
            syncing = false;
            ThemeManager.ApplyBackground(color);
            saveTimer.Stop();
            saveTimer.Start();
        }

        string[] channels = ["Settings.Red", "Settings.Green", "Settings.Blue"];
        for (int i = 0; i < 3; i++)
        {
            var slider = new Slider { Style = StyleOf("Oce.ScaleSlider"), Minimum = 0, Maximum = 255, TickFrequency = 1, Width = 220,
                Value = i == 0 ? initial.R : i == 1 ? initial.G : initial.B, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(slider, Loc.Get(channels[i]));
            slider.ValueChanged += (_, _) =>
            {
                if (syncing) return;
                Use(Color.FromRgb((byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value), fromSliders: true);
            };
            sliders[i] = slider;
            Row(p, Loc.Get(channels[i]), slider);
        }
        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (SettingsStore.ParseColor(hex.Text) is Color typed) Use(typed, fromText: true);
        };

        var presets = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (uint rgb in new uint[] { 0x000000, 0x111827, 0x0B1E33, 0x1E1B2E, 0x0F2A1D, 0x2B1B12, 0x3A3A3C, 0xF4F4F6 })
        {
            Color color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            var chip = new Button { Style = StyleOf("Oce.Button.Ghost"), Width = 30, Height = 30, Padding = new Thickness(3), MinWidth = 0,
                Margin = new Thickness(0, 0, 6, 6), ToolTip = SettingsStore.ColorToHex(color),
                Content = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(color), BorderThickness = new Thickness(1) } };
            ((Border)chip.Content).SetResourceReference(Border.BorderBrushProperty, "Set.Border");
            chip.Click += (_, _) => Use(color);
            presets.Children.Add(chip);
        }
        p.Children.Add(presets);
        Actions(p, Button(Loc.Get("Settings.BackgroundDefault"), (_, _) => Use(null), variant: "Secondary"));
    }

    /// The background of the theme itself (black when dark), shown while no colour of the user's own is set.
    private static Color DefaultBackground() => ThemeManager.IsLight ? Color.FromRgb(0xF4, 0xF4, 0xF6) : Colors.Black;
    private void AddChoice(Panel panel, string label, string[] choices, int selected, Action<int> changed)
    {
        var combo = new ComboBox { Width = 180, ItemsSource = choices, SelectedIndex = selected, Style = StyleOf("Oce.Select"),
            ItemContainerStyle = StyleOf("Oce.Select.Item"), VerticalAlignment = VerticalAlignment.Center };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); };
        Row(panel, label, combo);
    }
    /// A switch that reacts to the user's click only (Click, not Checked/Unchecked, so setting IsChecked from code
    /// never saves anything). changed returns what was actually stored, and the switch shows exactly that.
    private static ToggleButton Switch(bool value, Func<bool, bool> changed)
    {
        var toggle = new ToggleButton { Style = StyleOf("Oce.Switch"), IsChecked = value };
        toggle.Click += (_, _) => toggle.IsChecked = changed(toggle.IsChecked == true);
        return toggle;
    }
    /// For settings stored in the settings file: the switch follows the saved value (the old one if saving failed).
    private static ToggleButton SettingSwitch(bool value, Func<Settings, bool, Settings> set, Func<Settings, bool> get, Action<Settings>? saved = null) =>
        Switch(value, on =>
        {
            Settings? updated = SettingsStore.Update(x => set(x, on));
            if (updated is null) return !on;
            saved?.Invoke(updated);
            return get(updated);
        });
    private void AddToggle(Panel panel, string text, bool value, Func<Settings, bool, Settings> set, Func<Settings, bool> get, Action<Settings>? saved = null) =>
        Row(panel, text, SettingSwitch(value, set, get, saved));

    /// The scheduled task is asked off the UI thread (schtasks answers in well under a second; AutoStartService gives
    /// it 5 s at most); the hint says Activado / Desactivado, or why it could not be read or changed.
    private void AddStartupToggle(Panel panel)
    {
        ToggleButton? toggle = null;
        TextBlock? hint = null;
        bool enabled = false;
        void Show(bool state, string? error)
        {
            enabled = state;
            toggle!.IsChecked = state;
            toggle.IsEnabled = true;
            hint!.Text = error is not null ? DisplayError(error) : Loc.Get(state ? "Settings.On" : "Settings.Off");
            if (error is not null) hint.SetResourceReference(TextBlock.ForegroundProperty, "Oce.Danger");
            else hint.SetResourceReference(TextBlock.ForegroundProperty, "Set.Muted");
        }
        toggle = Switch(false, value =>
        {
            toggle!.IsEnabled = false;
            hint!.Text = Loc.Get("Settings.Applying");
            Task.Run(() => value ? AutoStartService.Enable() : AutoStartService.Disable()).ContinueWith(task =>
            {
                string? error = task.IsCompletedSuccessfully ? task.Result
                    : value ? "No se pudo registrar el inicio con Windows" : "No se pudo quitar el inicio con Windows";
                Show(error is null ? value : enabled, error);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return value;
        });
        toggle.IsEnabled = false;
        FlowRow row = Row(panel, Loc.Get("Settings.StartWithWindows"), toggle, Loc.Get("Settings.Checking"));
        hint = (TextBlock)((StackPanel)row.Children[1]).Children[1];
        if (Preview) { Show(true, null); return; }
        Task.Run(AutoStartService.Query).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) Show(task.Result.Enabled, task.Result.Error);
            else Show(false, AutoStartService.QueryFailedMessage);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    private void AddRingThemes(Settings s)
    {
        var card = Card(Loc.Get("Settings.RingColors"), Loc.Get("Settings.RingColorsHint"));
        var choices = new WrapPanel();
        Color[][] swatches =
        [
            [Color.FromRgb(232,73,29), Color.FromRgb(34,197,94), Color.FromRgb(229,230,25)],
            [Color.FromRgb(229,231,235), Color.FromRgb(156,163,175), Color.FromRgb(107,114,128)],
            [Color.FromRgb(14,165,233), Color.FromRgb(6,182,212), Color.FromRgb(99,102,241)],
            [Color.FromRgb(249,115,22), Color.FromRgb(239,68,68), Color.FromRgb(217,70,239)],
            [Color.FromRgb(163,230,53), Color.FromRgb(45,212,191), Color.FromRgb(232,121,249)],
        ];
        string[] names = ["Classic", "Mono", "Ocean", "Sunset", "Neon"];
        var sampleButtons = new List<ToggleButton>();
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var rings = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (Color color in swatches[i]) rings.Children.Add(new Ellipse { Width = 16, Height = 16, Margin = new Thickness(2), StrokeThickness = 3, Stroke = new SolidColorBrush(color), Fill = Brushes.Transparent });
            var contents = new StackPanel(); contents.Children.Add(rings);
            contents.Children.Add(new TextBlock { Text = names[i], FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
            var sample = new ToggleButton { Content = contents, IsChecked = (int)s.ColorTheme == i, Padding = new Thickness(10, 9, 10, 8), Margin = new Thickness(0, 0, 8, 8),
                Style = StyleOf("Oce.SampleChoice") };
            sampleButtons.Add(sample);
            sample.Checked += (_, _) =>
            {
                foreach (ToggleButton other in sampleButtons) if (!ReferenceEquals(other, sample)) other.IsChecked = false;
                var updated = SettingsStore.Update(x => x with { ColorTheme = (RingColorTheme)index });
                if (updated != null) { ThemeManager.Apply(updated.Theme, updated.ColorTheme); _apply(updated); }
            };
            choices.Children.Add(sample);
        }
        Inside(card).Children.Add(choices);
    }
    private void AddScale(Settings s)
    {
        var card = Card(Loc.Get("Settings.WidgetSize"));
        var slider = new Slider { Style = StyleOf("Oce.ScaleSlider"), Value = s.UiScale ?? 1, IsEnabled = s.UiScale is not null, Margin = new Thickness(0, 6, 0, 0) };
        var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            SettingsStore.Update(x => x with { UiScale = slider.IsEnabled ? slider.Value : null });
        };
        Closed += (_, _) => saveTimer.Stop();
        string ScaleText() => Loc.Format("Settings.ManualScale", slider.Value);
        TextBlock? value = null;
        var auto = SettingSwitch(s.UiScale is null, (x, on) =>
        {
            saveTimer.Stop();
            return x with { UiScale = on ? null : slider.Value };
        }, x => x.UiScale is null, updated =>
        {
            slider.IsEnabled = updated.UiScale is not null;
            value!.Text = updated.UiScale is null ? Loc.Get("Settings.AutoScale") : ScaleText();
            _apply(updated);
        });
        var row = Row(Inside(card), Loc.Get("Settings.Automatic"), auto, s.UiScale is null ? Loc.Get("Settings.AutoScale") : ScaleText());
        value = (TextBlock)((StackPanel)row.Children[1]).Children[1];
        Inside(card).Children.Add(slider);
        slider.ValueChanged += (_, _) =>
        {
            if (!slider.IsEnabled) return;
            value.Text = ScaleText();
            _apply(SettingsStore.Load() with { UiScale = slider.Value });
            saveTimer.Stop();
            saveTimer.Start();
        };
    }
    private void Agents()
    {
        var p = Inside(Card(string.Empty));
        Settings settings = SettingsStore.Load();
        bool first = true;
        foreach (AiProviderId id in AiDetector.All)
        {
            if (!first) p.Children.Add(new Border { Style = StyleOf("Oce.Separator"), Margin = new Thickness(-20, 10, -20, 10) });
            first = false;
            string name = id.ToString();
            AgentStatus status = _agentStatus(id);

            var row = new DockPanel { LastChildFill = true };
            var logo = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Child = Glyph(ProviderLogo(id), 18), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            logo.SetResourceReference(Border.BackgroundProperty, "Set.Secondary");
            logo.SetResourceReference(Border.BorderBrushProperty, "Set.Border");
            DockPanel.SetDock(logo, Dock.Left); row.Children.Add(logo);

            bool visible = !settings.Providers.TryGetValue(name, out ProviderVisibility v) || v != ProviderVisibility.Hide;
            var show = SettingSwitch(visible, (x, on) =>
                {
                    var map = x.Providers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    map[name] = on ? ProviderVisibility.Show : ProviderVisibility.Hide;
                    return x with { Providers = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) };
                },
                x => !x.Providers.TryGetValue(name, out ProviderVisibility stored) || stored != ProviderVisibility.Hide, _apply);
            show.ToolTip = Loc.Get("Settings.ShowInPanel");
            var showBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            showBox.Children.Add(Muted(Loc.Get("Settings.Show"), new Thickness(0, 0, 8, 1), 12.5));
            ((TextBlock)showBox.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            showBox.Children.Add(show);
            DockPanel.SetDock(showBox, Dock.Right); row.Children.Add(showBox);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (id is AiProviderId.Claude or AiProviderId.Codex or AiProviderId.Cursor)
            {
                int extra = settings.AccountsOf(AiProviderSettings.Key(id)).Length;
                Button accounts = SmallButton(extra > 0 ? Loc.Format("Settings.AccountsCount", extra + 1) : Loc.Get("Settings.Accounts"),
                    (_, _) => { _accountEditor = _accountEditor == id ? null : id; RenderPage(); });
                accounts.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(accounts);
            }
            if (id is AiProviderId.DeepSeek or AiProviderId.OpenRouter)
                actions.Children.Add(SmallButton(Loc.Get("Settings.ApiKey"), (_, _) => { _keyEditor = _keyEditor == id ? null : id; RenderPage(); }));
            else if (id is AiProviderId.Claude or AiProviderId.Codex or AiProviderId.Cursor && status.Kind is "session" or "missing")
            {
                actions.Children.Add(SmallButton(Loc.Get("Settings.Connect"), (_, _) =>
                {
                    if (id == AiProviderId.Claude)
                    {
                        // The same sign-in as clicking the ring while signed out (ClaudeSessionRenewer.StartLogin).
                        string? loginError = ClaudeSessionRenewer.StartLogin();
                        if (loginError is not null) MessageBox.Show(DisplayError(loginError));
                        else (Application.Current as App)?.WatchClaudeSignIn();
                        return;
                    }
                    string name = id == AiProviderId.Codex ? "codex" : "cursor-agent";
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var candidates = new List<string>
                    {
                        IOPath.Combine(profile, ".local", "bin", name + ".exe"),
                        IOPath.Combine(profile, ".cursor", "bin", name + ".exe"),
                        IOPath.Combine(appData, "npm", name + ".cmd"),
                        IOPath.Combine(appData, "npm", name + ".exe"),
                    };
                    string? path = Environment.GetEnvironmentVariable("PATH");
                    if (!string.IsNullOrWhiteSpace(path))
                        foreach (string folder in path.Split(IOPath.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (IOPath.IsPathFullyQualified(folder))
                            {
                                candidates.Add(IOPath.Combine(folder, name + ".exe"));
                                candidates.Add(IOPath.Combine(folder, name + ".cmd"));
                            }
                    string? cli = candidates.FirstOrDefault(File.Exists);
                    if (cli is null) { MessageBox.Show(Loc.Get("Settings.CliMissing")); return; }
                    const string login = "login";
                    bool script = IOPath.GetExtension(cli).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
                    string application = script ? IOPath.Combine(Environment.SystemDirectory, "cmd.exe") : cli;
                    string arguments = script ? $"/d /s /c \"{UnelevatedLauncher.Quote(cli)} {login}\"" : login;
                    var result = UnelevatedLauncher.Run(application, arguments, profile, hidden: false, wait: null);
                    if (!result.Started) MessageBox.Show(result.Error is string error ? DisplayError(error) : Loc.Get("Settings.LoginFailed"));
                }));
            }
            if (status.Kind == "error" || (status.Kind == "session" && id is not (AiProviderId.Claude or AiProviderId.Codex or AiProviderId.Cursor)))
            {
                if (actions.Children.Count > 0) ((FrameworkElement)actions.Children[^1]).Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(SmallButton(Loc.Get("Settings.Retry"), async (_, _) => { await _retry(id); RenderPage(); }));
            }
            var details = new FlowRow { MinTextWidth = 190 };
            details.Children.Add(actions);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var titleLine = new WrapPanel();
            titleLine.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 1) });
            titleLine.Children.Add(StatusBadge(status));
            text.Children.Add(titleLine);
            if (status.Message is not null && status.Kind is "error" or "session")
            {
                var detail = Muted((Loc.Message(status.Message) ?? status.Message) +
                    (status.FailedAt is DateTimeOffset failedAt ? " · " + failedAt.ToLocalTime().ToString("g", Loc.Culture) : ""), new Thickness(0, 3, 0, 0), 12);
                detail.TextTrimming = TextTrimming.CharacterEllipsis; detail.MaxHeight = 34; detail.ToolTip = detail.Text;
                if (id == AiProviderId.Claude && status.Kind == "error") detail.Foreground = (Brush)Application.Current.FindResource("Oce.Danger");
                text.Children.Add(detail);
            }
            details.Children.Add(text);
            row.Children.Add(details);
            p.Children.Add(row);

            if (_keyEditor == id) p.Children.Add(KeyEditor(id, name));
            if (_accountEditor == id) p.Children.Add(AccountEditor(id, settings));
        }
    }
    /// More accounts of Claude Code, Codex or Cursor: each one is the configuration folder that account signs in to
    /// (CLAUDE_CONFIG_DIR, CODEX_HOME, Cursor's --user-data-dir) and a name for the tab of its card.
    private StackPanel AccountEditor(AiProviderId id, Settings settings)
    {
        string provider = AiProviderSettings.Key(id);
        var editor = new StackPanel { Margin = new Thickness(46, 12, 0, 0) };
        editor.Children.Add(Muted(Loc.Get("Settings.AccountsHint." + provider), new Thickness(0, 0, 0, 8), 12));
        ImmutableArray<ExtraAccount> accounts = settings.AccountsOf(provider);
        var main = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        main.Children.Add(new TextBlock { Text = Loc.Get("Account.Default"), FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        editor.Children.Add(main);
        foreach (ExtraAccount account in accounts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            Button remove = SmallButton(Loc.Get("Settings.Delete"), (_, _) =>
            {
                Settings? updated = SettingsStore.Update(x => WithAccounts(x, provider, x.AccountsOf(provider).Remove(account)));
                if (updated is not null) { _apply(updated); RenderPage(); }
            });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = account.Name, FontWeight = FontWeights.Medium });
            var folder = Muted(account.Folder, new Thickness(0, 1, 12, 0), 11.5);
            folder.TextTrimming = TextTrimming.CharacterEllipsis;
            folder.TextWrapping = TextWrapping.NoWrap;
            folder.ToolTip = account.Folder;
            text.Children.Add(folder);
            row.Children.Add(text);
            editor.Children.Add(row);
        }

        if (accounts.Length >= SettingsStore.AccountLimit) return editor;
        var nameBox = new TextBox { Style = StyleOf("Oce.Input"), MaxLength = 40, Width = 180 };
        AutomationProperties.SetName(nameBox, Loc.Get("Settings.AccountName"));
        var add = Button(Loc.Get("Settings.AddAccount"), (_, _) =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { MessageBox.Show(Loc.Get("Settings.AccountNameMissing")); return; }
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("Settings.AccountFolder." + provider) };
            if (dialog.ShowDialog(this) != true) return;
            string folder = dialog.FolderName;
            string expected = provider switch
            {
                AiProviderSettings.Claude => IOPath.Combine(folder, ".credentials.json"),
                AiProviderSettings.Codex => IOPath.Combine(folder, "auth.json"),
                _ => IOPath.Combine(folder, "User", "globalStorage", "state.vscdb"),
            };
            if (!File.Exists(expected) && MessageBox.Show(Loc.Format("Settings.AccountFolderEmpty", IOPath.GetFileName(expected)),
                    "Open Control Edge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Settings? updated = SettingsStore.Update(x => WithAccounts(x, provider, x.AccountsOf(provider).Add(new ExtraAccount(name, folder))));
            if (updated is not null) { _apply(updated); RenderPage(); }
        }, true);
        add.Margin = new Thickness(8, 0, 0, 0);
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        addRow.Children.Add(nameBox);
        addRow.Children.Add(add);
        editor.Children.Add(FieldLabel(Loc.Get("Settings.AccountName")));
        editor.Children.Add(addRow);
        return editor;
    }

    private static Settings WithAccounts(Settings settings, string provider, ImmutableArray<ExtraAccount> accounts)
    {
        var map = settings.Accounts.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (accounts.IsEmpty) map.Remove(provider);
        else map[provider] = accounts;
        return settings with { Accounts = map.ToFrozenDictionary(StringComparer.Ordinal) };
    }

    // ───────────────────────────── Modo juego ─────────────────────────────

    private void GameMode()
    {
        Settings s = SettingsStore.Load();
        GameModeOptions game = s.GameMode;
        var p = Inside(Card(Loc.Get("Settings.GameMode"), Loc.Get("Settings.GameModeHint")));
        AddToggle(p, Loc.Get("Settings.GameModeEnable"), game.Enabled,
            (x, on) => x with { GameMode = x.GameMode with { Enabled = on } }, x => x.GameMode.Enabled, _apply);
        p.Children.Add(Muted(Loc.Get(GameModeService.IsActive ? "Settings.GameModeIsOn" : "Settings.GameModeIsOff"), new Thickness(0, 4, 0, 0), 12));

        var what = Inside(Card(Loc.Get("Settings.GameModeWhat")));
        GamePowerPlan[] plans = [GamePowerPlan.Balanced, GamePowerPlan.HighPerformance, GamePowerPlan.Keep];
        AddChoice(what, Loc.Get("Settings.PowerPlan"), [Loc.Get("Settings.PowerBalanced"), Loc.Get("Settings.PowerHigh"), Loc.Get("Settings.PowerKeep")],
            Array.IndexOf(plans, game.PowerPlan), i =>
            {
                Settings? updated = SettingsStore.Update(x => x with { GameMode = x.GameMode with { PowerPlan = plans[i] } });
                if (updated is not null) _apply(updated);
            });
        AddToggle(what, Loc.Get("Settings.GameBarOff"), game.DisableGameBar,
            (x, on) => x with { GameMode = x.GameMode with { DisableGameBar = on } }, x => x.GameMode.DisableGameBar, _apply);
        AddToggle(what, Loc.Get("Settings.Relaunch"), game.Relaunch,
            (x, on) => x with { GameMode = x.GameMode with { Relaunch = on } }, x => x.GameMode.Relaunch, _apply);

        AddNameList(Loc.Get("Settings.GameProcesses"), Loc.Get("Settings.GameProcessesHint"), game.Processes, GameModeOptions.DefaultProcesses,
            GameModeService.IsValidProcessName, list => x => x with { GameMode = x.GameMode with { Processes = list } }, running: true);
        AddNameList(Loc.Get("Settings.GameServices"), Loc.Get("Settings.GameServicesHint"), game.Services, GameModeOptions.DefaultServices,
            GameModeService.IsValidServiceName, list => x => x with { GameMode = x.GameMode with { Services = list } }, running: false);
    }

    /// One name per line (programs or services); invalid or protected names are left out and listed.
    private void AddNameList(string title, string hint, ImmutableArray<string> current, ImmutableArray<string> defaults,
        Func<string, bool> valid, Func<ImmutableArray<string>, Func<Settings, Settings>> store, bool running)
    {
        var p = Inside(Card(title, hint));
        var box = new TextBox { Style = StyleOf("Oce.Textarea"), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 132,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join(Environment.NewLine, current) };
        AutomationProperties.SetName(box, title);
        p.Children.Add(box);
        var status = Muted("", new Thickness(0, 6, 0, 0), 12);
        status.Visibility = Visibility.Collapsed;
        p.Children.Add(status);

        void Save(IEnumerable<string> lines)
        {
            var names = lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rejected = names.Where(n => !valid(n)).ToList();
            ImmutableArray<string> kept = [.. names.Where(valid).Take(64)];
            Settings? updated = SettingsStore.Update(store(kept));
            if (updated is null) return;
            _apply(updated);
            box.Text = string.Join(Environment.NewLine, kept);
            status.Text = rejected.Count == 0 ? Loc.Get("Settings.Saved") : Loc.Format("Settings.NamesRejected", string.Join(", ", rejected));
            status.Visibility = Visibility.Visible;
        }

        var buttons = new List<Button>
        {
            Button(Loc.Get("Settings.Save"), (_, _) => Save(box.Text.Split('\n')), true),
            Button(Loc.Get("Settings.Defaults"), (_, _) => Save(defaults), variant: "Secondary"),
        };
        if (running)
        {
            var pickHolder = new StackPanel();
            p.Children.Insert(p.Children.IndexOf(box) + 1, pickHolder);
            AddRunningPicker(pickHolder, box);
        }
        Actions(p, buttons.ToArray());
    }

    /// "Añadir un programa abierto": the programs of this session, to add to the list without typing the name.
    private static void AddRunningPicker(Panel panel, TextBox box)
    {
        var pick = new ComboBox { Width = 220, Style = StyleOf("Oce.Select"), ItemContainerStyle = StyleOf("Oce.Select.Item"),
            ToolTip = Loc.Get("Settings.AddRunning") };
        AutomationProperties.SetName(pick, Loc.Get("Settings.AddRunning"));
        pick.DropDownOpened += (_, _) => { if (pick.Items.Count == 0) pick.ItemsSource = GameModeService.RunningProgramNames(); };
        pick.SelectionChanged += (_, _) =>
        {
            if (pick.SelectedItem is not string name) return;
            if (!box.Text.Split('\n').Any(l => l.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                box.Text = box.Text.TrimEnd() + (box.Text.Trim().Length > 0 ? Environment.NewLine : "") + name;
        };
        Row(panel, Loc.Get("Settings.AddRunning"), pick);
    }

    private static Button SmallButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = StyleOf("Oce.Button.Small") };
        b.Click += click; return b;
    }
    /// Outline badge with a coloured dot: connected (and the plan), not installed, hidden, no session or sync error.
    private static Border StatusBadge(AgentStatus status)
    {
        (string text, string? dot) = status.Kind switch
        {
            "connected" => (Loc.Get("Settings.Connected") + (string.IsNullOrWhiteSpace(status.Plan) ? "" : " · " + status.Plan), "Oce.Success"),
            "missing" => (Loc.Get("Settings.NotInstalled"), null),
            "hidden" => (Loc.Get("Settings.HiddenAgent"), null),
            "session" => (Loc.Get("Settings.NoSession"), "Oce.Warning"),
            _ => (Loc.Get("Settings.SyncError"), "Oce.Danger"),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot is not null) content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = (Brush)Application.Current.FindResource(dot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, dot is null ? "Set.Muted" : "Set.Foreground");
        content.Children.Add(label);
        return new Border { Style = StyleOf("Oce.Badge"), Child = content };
    }
    /// API key field of DeepSeek / OpenRouter, opened from the row's "Clave API" button.
    private DockPanel KeyEditor(AiProviderId id, string name)
    {
        var editor = new DockPanel { Margin = new Thickness(46, 12, 0, 0) };
        var box = new PasswordBox { Style = StyleOf("Oce.Password"), ToolTip = Loc.Format("Settings.ApiKeyFor", name) };
        var save = Button(Loc.Get("Settings.Save"), async (_, _) =>
        {
            // Pasted keys often carry a space or a line break that the provider would reject.
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(box.Password.Trim());
            bool ok = ProviderKeyStore.Save(name.ToLowerInvariant(), bytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            MessageBox.Show(Loc.Get(ok ? "Settings.KeySaved" : "Settings.KeySaveFailed"));
            if (ok) { _keyEditor = null; await _retry(id); RenderPage(); }
        }, true);
        var delete = Button(Loc.Get("Settings.Delete"), async (_, _) => { ProviderKeyStore.Delete(name.ToLowerInvariant()); await _retry(id); RenderPage(); });
        save.Margin = new Thickness(8, 0, 0, 0); delete.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(delete, Dock.Right); DockPanel.SetDock(save, Dock.Right);
        editor.Children.Add(delete); editor.Children.Add(save); editor.Children.Add(box);
        return editor;
    }
    private void About()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        var p = Inside(Card("Open Control Edge", $"{version} · {build} · .NET {Environment.Version.Major} · win-x64"));
        bool pawnInstalled = false; try { pawnInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { }
        var pawnState = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // Off until the service answers (asked off the UI thread just below).
        var pawnLed = new Ellipse { Style = StyleOf("Oce.Led"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };
        var pawnLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center,
            Text = Loc.Get(pawnInstalled ? "Settings.Checking" : "Settings.PawnMissing") };
        pawnState.Children.Add(pawnLed);
        pawnState.Children.Add(pawnLabel);
        if (pawnInstalled)
            _ = Task.Run(() => PawnIoInstaller.ServiceRunning).ContinueWith(task => Dispatcher.Invoke(() =>
            {
                pawnLed.Style = StyleOf(task.Result ? "Oce.Led.On" : "Oce.Led");
                pawnLabel.Text = Loc.Get(task.Result ? "Settings.PawnRunning" : "Settings.PawnStopped");
            }));
        UIElement pawnControl = pawnState;
        if (!pawnInstalled)
        {
            var install = new StackPanel { Orientation = Orientation.Horizontal };
            install.Children.Add(pawnState); pawnState.Margin = new Thickness(0, 0, 12, 0);
            install.Children.Add(SmallButton(Loc.Get("Settings.DownloadInstall"), async (sender, _) => { var b = (Button)sender; b.IsEnabled = false; b.Content = Loc.Get("Settings.PawnInstalling"); string? error = await PawnIoInstaller.InstallAsync(); MessageBox.Show(error is null ? Loc.Get("Settings.PawnInstalled") : DisplayError(error)); RenderPage(); }));
            pawnControl = install;
        }
        Row(p, "PawnIO", pawnControl, Loc.Get("Settings.PawnHint"));
        bool sensors = _sensorsAvailable();
        Row(p, Loc.Get("Settings.TemperatureReadings"), new TextBlock { Text = Loc.Get(sensors ? "Settings.Yes" : "Settings.No"), VerticalAlignment = VerticalAlignment.Center });
        var links = new[] { (Loc.Get("Settings.Repository"), "https://github.com/danielfinchdev/open-control-edge"), (Loc.Get("Settings.License"), "https://github.com/danielfinchdev/open-control-edge/blob/main/LICENSE"), (Loc.Get("Settings.ThirdParty"), "https://github.com/danielfinchdev/open-control-edge/blob/main/THIRD-PARTY-NOTICES.txt") }
            .Select(link => Button(link.Item1, (_, _) => OpenUnelevated(link.Item2))).ToList();
        links.Add(Button(Loc.Get("Settings.OpenLog"), (_, _) => { string log = IOPath.Combine(DataFolder.Path, "widget.log"); UnelevatedLauncher.Run(IOPath.Combine(Environment.SystemDirectory, "explorer.exe"), "/select," + UnelevatedLauncher.Quote(log), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false, null); }));
        p.Children.Add(new Border { Style = StyleOf("Oce.Separator") });
        Actions(p, links.ToArray()).Margin = new Thickness(0);
    }
    private void Updates()
    {
        var p = Inside(Card(Loc.Get("Settings.Nav.Updates")));
        var status = Muted(_release is null
            ? _noUpdateAvailable ? Loc.Get("Settings.NoUpdates") : Loc.Format("Settings.CurrentVersion", typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown")
            : Loc.Format("Settings.UpdateFound", _release.Tag), new Thickness(0, 0, 0, 4));
        p.Children.Add(status);
        var notes = new TextBox { Style = StyleOf("Oce.Textarea"), Text = _release?.Notes ?? "", IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        if (_release is not null) p.Children.Add(notes);
        var install = Button(Loc.Get("Settings.DownloadInstall"), async (sender, _) =>
        {
            if (_release is null) return;
            await InstallUpdateAsync(_release, status, (Button)sender);
        }, true);
        var check = Button(Loc.Get("Settings.CheckUpdates"), async (_, _) =>
        {
            status.Text = Loc.Get("Settings.Searching");
            UpdateCheckResult result = await UpdateService.CheckAsync();
            if (result.Error is not null) status.Text = DisplayError(result.Error);
            else if (result.Release is null) { _release = null; _noUpdateAvailable = true; RenderPage(); }
            else { _release = result.Release; _noUpdateAvailable = false; RenderPage(); }
        }, _release is null);
        if (_release is not null) Actions(p, install, check); else Actions(p, check);
        p.Children.Add(new Border { Style = StyleOf("Oce.Separator") });
        Settings settings = SettingsStore.Load();
        AddToggle(p, Loc.Get("Settings.AutoCheck"), settings.AutoCheckUpdates,
            (x, enabled) => x with { AutoCheckUpdates = enabled, LastAutoUpdateCheck = enabled ? null : x.LastAutoUpdateCheck },
            x => x.AutoCheckUpdates, _apply);
    }
    private readonly List<FeedbackImage> _feedbackImages = new();

    /// Feedback without any account (FeedbackService: mailed with the screenshots) or as a GitHub issue.
    private void Feedback()
    {
        var p = Inside(Card(Loc.Get("Settings.TellUs"), Loc.Get("Settings.FeedbackHint")));
        string[] types = [Loc.Get("Settings.Type.Bug"), Loc.Get("Settings.Type.Idea"), Loc.Get("Settings.Type.Other")];
        var type = new ComboBox { ItemsSource = types, SelectedIndex = 0, Width = 180, Style = StyleOf("Oce.Select"), ItemContainerStyle = StyleOf("Oce.Select.Item") };
        Row(p, Loc.Get("Settings.Type"), type);
        p.Children.Add(FieldLabel(Loc.Get("Settings.Message")));
        var body = new TextBox { Style = StyleOf("Oce.Textarea"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = FeedbackService.MaxMessageLength };
        AutomationProperties.SetName(body, Loc.Get("Settings.Message"));
        p.Children.Add(body);
        p.Children.Add(FieldLabel(Loc.Get("Settings.Contact")));
        var contact = new TextBox { Style = StyleOf("Oce.Input"), MaxLength = 120 };
        AutomationProperties.SetName(contact, Loc.Get("Settings.Contact"));
        p.Children.Add(contact);

        // Screenshots: thumbnails with a remove button, at most FeedbackService.MaxImages.
        p.Children.Add(FieldLabel(Loc.Get("Settings.Screenshots")));
        var thumbnails = new WrapPanel();
        var addRow = new WrapPanel();
        void ShowImages()
        {
            thumbnails.Children.Clear();
            foreach (FeedbackImage image in _feedbackImages.ToList())
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                using (var stream = new MemoryStream(image.Bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 160;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                var tile = new Grid { Width = 120, Height = 76, Margin = new Thickness(0, 0, 8, 8), ToolTip = image.Name };
                tile.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true,
                    Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill } });
                Button remove = IconButton(Icons.Close, Loc.Get("Settings.Delete"), () => { _feedbackImages.Remove(image); ShowImages(); });
                remove.Width = remove.Height = 24;
                remove.HorizontalAlignment = HorizontalAlignment.Right;
                remove.VerticalAlignment = VerticalAlignment.Top;
                remove.SetResourceReference(BackgroundProperty, "Set.Background");
                tile.Children.Add(remove);
                thumbnails.Children.Add(tile);
            }
            foreach (Button b in addRow.Children.OfType<Button>()) b.IsEnabled = _feedbackImages.Count < FeedbackService.MaxImages;
        }
        p.Children.Add(thumbnails);
        Button capture = Button(Loc.Get("Settings.CaptureScreen"), async (_, _) =>
        {
            // The settings window steps aside for the shot, then comes back.
            WindowState previous = WindowState;
            WindowState = WindowState.Minimized;
            await Task.Delay(450);
            FeedbackImage? shot = await Task.Run(FeedbackService.CaptureScreen);
            WindowState = previous;
            BringToFront();
            if (shot is not null && _feedbackImages.Count < FeedbackService.MaxImages) _feedbackImages.Add(shot);
            ShowImages();
        }, variant: "Secondary");
        Button pick = Button(Loc.Get("Settings.AddImage"), (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.Get("Settings.ImageFilter") + "|*.png;*.jpg;*.jpeg", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            foreach (string file in dialog.FileNames)
            {
                if (_feedbackImages.Count >= FeedbackService.MaxImages) break;
                if (FeedbackService.LoadImage(file) is FeedbackImage image) _feedbackImages.Add(image);
                else MessageBox.Show(Loc.Format("Settings.ImageRejected", IOPath.GetFileName(file)));
            }
            ShowImages();
        }, variant: "Secondary");
        capture.Margin = pick.Margin = new Thickness(0, 0, 8, 6);
        addRow.Children.Add(capture);
        addRow.Children.Add(pick);
        p.Children.Add(addRow);
        p.Children.Add(Muted(Loc.Get("Settings.ScreenshotsHint"), new Thickness(0, 2, 0, 0), 12));
        ShowImages();

        var status = Muted("", new Thickness(0, 10, 0, 0));
        status.Visibility = Visibility.Collapsed;
        string Payload() => $"Type: {type.Text}\nVersion: {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown"}\nOS: {Environment.OSVersion.VersionString}\n\n{body.Text}";
        Button send = Button(Loc.Get("Settings.SendFeedback"), async (sender, _) =>
        {
            var button = (Button)sender;
            button.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            status.Text = Loc.Get("Settings.Sending");
            string? error = await FeedbackService.SendAsync(type.Text, body.Text, contact.Text, _feedbackImages.ToList());
            if (error is null)
            {
                status.Text = Loc.Get("Settings.FeedbackSent");
                body.Text = string.Empty;
                _feedbackImages.Clear();
                ShowImages();
            }
            else status.Text = DisplayError(error);
            button.IsEnabled = true;
        }, true);
        send.IsEnabled = FeedbackService.CanSendWithoutAccount;
        Actions(p,
            send,
            Button(Loc.Get("Settings.OpenForm"), (_, _) =>
            {
                // GitHub cannot receive the images through the address: the first one goes to the clipboard to paste.
                if (_feedbackImages.FirstOrDefault() is FeedbackImage first)
                {
                    try
                    {
                        using var stream = new MemoryStream(first.Bytes);
                        Clipboard.SetImage(System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad));
                    }
                    catch (Exception ex) { Log.Warn("Feedback", "portapapeles: " + ex.GetType().Name); }
                }
                OpenUnelevated(FeedbackService.GitHubIssueUri(type.Text, body.Text).AbsoluteUri);
                status.Visibility = Visibility.Visible;
                status.Text = Loc.Get(_feedbackImages.Count > 0 ? "Settings.GitHubOpenedImage" : "Settings.GitHubOpened");
            }, variant: "Secondary"),
            Button(Loc.Get("Settings.CopyDraft"), (_, _) => Clipboard.SetText(Payload()), variant: "Ghost")).Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(status);
        p.Children.Add(Muted(Loc.Get(FeedbackService.CanSendWithoutAccount ? "Settings.FeedbackPrivacy" : "Settings.FeedbackNoForm"),
            new Thickness(0, 10, 0, 0), 12));
    }

    private static void OpenUnelevated(string url) =>
        UnelevatedLauncher.Run(IOPath.Combine(Environment.SystemDirectory, "explorer.exe"), UnelevatedLauncher.Quote(url),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), hidden: false, wait: null);
    private static TextBlock FieldLabel(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };

    /// A service message in the interface language (Loc: Strings.*.xaml).
    private static string DisplayError(string message) => Loc.Message(message) ?? message;

    internal static void SaveSnapshots(string directory)
    {
        // One agent in each state, so the Agents page shows every badge and button.
        static AgentStatus SampleStatus(AiProviderId id) => id switch
        {
            AiProviderId.Claude => new AgentStatus("connected", "Max", null, null),
            AiProviderId.Codex => new AgentStatus("connected", "Plus", null, null),
            AiProviderId.Cursor => new AgentStatus("session", null, AiDetector.CursorLoginMessage, null),
            AiProviderId.DeepSeek => new AgentStatus("error", null, ProviderKeyStore.InvalidKeyMessage, DateTimeOffset.Now.AddMinutes(-12)),
            AiProviderId.OpenRouter => new AgentStatus("connected", null, null, null),
            _ => new AgentStatus("missing", null, null, null),
        };
        static string FileName(string category) => category.Replace(' ', '_').Replace('ó', 'o').Replace('í', 'i').ToLowerInvariant();
        // Work areas in DIPs (screen minus taskbar): the reference 1080p at 100 %, then 1366×768 at 100 %,
        // 1080p at 125 % and 1440p at 100 %, the same as the panel's scale shots.
        (string Name, double Width, double Height)[] screens = [("1366x768", 1366, 728), ("1080p_125", 1536, 824), ("1440p", 2560, 1392)];
        var release = new UpdateRelease(new Version(2, 2, 0), "v2.2.0",
            "Novedades de ejemplo:\n• Ventana de ajustes redimensionable.\n• Recuerda su tamaño y posición.\n• Corrige textos cortados en pantallas pequeñas.\n• Otras mejoras menores.",
            new Uri("https://example.invalid/OpenControlEdge.zip"), "sha256:0");
        foreach ((AppTheme theme, string themeName) in new[] { (AppTheme.Dark, "dark"), (AppTheme.Light, "light") })
        foreach ((UiLanguage language, string languageName) in new[] { (UiLanguage.Spanish, "es"), (UiLanguage.English, "en") })
        {
            ThemeManager.Apply(theme, RingColorTheme.Classic);
            Loc.Apply(language);
            var window = new SettingsWindow(static () => Task.CompletedTask, static _ => { }, SampleStatus, static _ => Task.CompletedTask)
                { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Preview = true };
            window.PreviewWorkArea(1920, 1040);
            window.Show();
            void Save(string file, string check)
            {
                window.RenderPage(); window.UpdateLayout();
                // Pages with lists (rings of each view, programs and services, screenshots) are meant to scroll.
                if (window._category is "Personalización" or "Modo juego" or "Feedback" or "Agentes") check = "";
                if (check.Length > 0 && window._scroll.ExtentHeight > window._scroll.ViewportHeight + 0.5)
                    throw new InvalidDataException($"settings page scrolls at its default size: {check} " +
                        $"({window._scroll.ExtentHeight:0} > {window._scroll.ViewportHeight:0})");
                Snapshot.SaveElement((FrameworkElement)window.Content, IOPath.Combine(directory, file));
            }
            foreach (string category in Categories)
            {
                window._category = category;
                Save($"settings_{themeName}_{languageName}_{FileName(category)}.png", $"{category} {themeName} {languageName}");
            }
            window._category = "Actualizaciones"; window._release = release;
            Save($"settings_{themeName}_{languageName}_actualizaciones_release.png", $"release {themeName} {languageName}");
            window._category = "General";
            Save($"settings_{themeName}_{languageName}_update_banner.png", "");
            window._release = null;
            window._category = "Personalización"; window._editingView = WidgetView.Pc;
            Save($"settings_{themeName}_{languageName}_personalizacion_vista_pc.png", "");
            window._editingView = null;
            window._category = "Agentes"; window._accountEditor = AiProviderId.Claude;
            Save($"settings_{themeName}_{languageName}_agentes_cuentas.png", "");
            window._accountEditor = null;
            if (language == UiLanguage.Spanish)
            {
                foreach ((string screen, double workWidth, double workHeight) in screens)
                {
                    window.PreviewWorkArea(workWidth, workHeight);
                    string zoom = window.Zoom.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    foreach (string category in Categories)
                    {
                        window._category = category;
                        Save($"settings_{screen}_x{zoom}_{themeName}_{FileName(category)}.png", $"{category} {screen} {themeName}");
                    }
                    window._category = "Actualizaciones"; window._release = release;
                    Save($"settings_{screen}_x{zoom}_{themeName}_actualizaciones_release.png", $"release {screen} {themeName}");
                    window._release = null;
                }
                // Resized by hand at zoom 1: as narrow and as short as allowed, and wide.
                window.PreviewWorkArea(1920, 1040);
                foreach ((string shape, double width, double height) in new[] { ("narrow", 0.0, 0.0), ("wide", 1240.0, 760.0) })
                {
                    window.PreviewSize(width, height);
                    foreach (string category in Categories)
                    {
                        window._category = category;
                        Save($"settings_resized_{shape}_{themeName}_{FileName(category)}.png", "");
                    }
                    window._category = "Agentes"; window._keyEditor = AiProviderId.DeepSeek;
                    window.RenderPage(); window.UpdateLayout(); window._scroll.ScrollToEnd();
                    Save($"settings_resized_{shape}_{themeName}_agentes_api_key.png", "");
                    window._scroll.ScrollToHome(); window._keyEditor = null;
                }
                window.PreviewWorkArea(1920, 1040);
            }
            if (theme == AppTheme.Dark && language == UiLanguage.Spanish)
            {
                window._category = "Agentes"; window._keyEditor = AiProviderId.DeepSeek;
                Save("settings_dark_es_agentes_api_key.png", "agentes api key");
                window._keyEditor = null;
            }
            window.Close();
        }
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic); Loc.Apply(UiLanguage.Spanish);
    }
}
