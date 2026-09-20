using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using EdgeWidget.Interop;
using EdgeWidget.Services;
using EdgeWidget.Ui;
using EdgeWidget.Views;

namespace EdgeWidget;

public partial class App : Application
{
    private const string MutexName = @"Local\EdgeWidget.SingleInstance.7F3C2A1E";

    /// Scheduled task installed by tools\claude-sesion.ps1. It renews the OAuth token that this widget
    /// reads and opens Claude Code. It runs as the plain user, never with this process's rights.
    private const string RenewTaskName = "Claude - Mantener sesion";

    /// The Claude Code desktop app, opened through Explorer so it does not inherit the administrator token.
    private const string ClaudeAppId = @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude";
    // Two cadences for each source: the fast one only while the pinned panel is actually on screen, the slow
    // one the rest of the time. The sensors never stop, so "Máxima de la sesión" also catches the peaks
    // nobody was watching — the boot spike above all.
    private static readonly TimeSpan UsageVisibleInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UsageHiddenInterval = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan SensorVisibleInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SensorHiddenInterval = TimeSpan.FromMinutes(1);

    /// The widget starts with the session, right inside the burst of programs that makes this laptop run hot.
    /// For the first few minutes the sensors are read often enough to catch that peak whatever the panel is doing.
    private static readonly TimeSpan SensorWarmupInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromMinutes(3);

    private readonly ClaudeUsageService _claude = new();
    private readonly CodexUsageService _codex = new();
    private readonly GrokUsageService _grok = new();
    private Mutex? _mutex;
    private HardwareSensorService? _sensors;
    private EdgeWindow? _edge;
    private TrayIcon? _tray;
    private TrayMenuWindow? _menu;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _sensorTimer;
    private DispatcherTimer? _warmupTimer;
    private bool _warmingUp = true;
    private Task? _usageRefresh;
    private Task? _sensorRefresh;
    private ClaudeSnapshot? _lastClaude;
    private CodexSnapshot? _lastCodex;
    private GrokSnapshot? _lastGrok;
    private CpuSnapshot? _lastCpu;
    private GpuSnapshot? _lastGpu;
    private string? _lastStatus;
    private PanelMode _panelMode;
    private bool _panelExpanded;
    private bool _manualRefresh;
    private bool _renewing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Dispatcher", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("AppDomain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Task", args.Exception);
            args.SetObserved();
        };

        int snapshotArg = Array.IndexOf(e.Args, "--snapshot");
        if (snapshotArg >= 0 && snapshotArg + 1 < e.Args.Length)
        {
            try { Snapshot.Run(e.Args[snapshotArg + 1]); }
            catch (Exception ex) { Log.Error("Snapshot", ex); }
            Shutdown();
            return;
        }

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        _panelMode = SettingsStore.Load().PanelMode;
        Log.Info("App", $"started (panel {_panelMode})");

        // Timers exist before the window is shown: a pinned panel expands during Show() and sets the cadence.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UsageHiddenInterval };
        _refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        _sensorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SensorHiddenInterval };
        _sensorTimer.Tick += async (_, _) => await RefreshSensorsAsync();

        _sensors = new HardwareSensorService();
        _edge = new EdgeWindow(_sensors.SessionStart, _panelMode);
        _lastCodex = _codex.Initial();
        _edge.SetCodex(_lastCodex);
        _lastGrok = _grok.Read();
        _edge.SetGrok(_lastGrok);
        _edge.ExpandedChanged += expanded =>
        {
            _panelExpanded = expanded;
            // A fresh reading the moment the panel opens, so a hover never shows a minute-old temperature.
            if (expanded) _ = RefreshSensorsAsync();
            UpdateCadence();
        };
        _edge.ModeChangeRequested += SetPanelMode;
        _edge.RefreshRequested += () => _ = RefreshEverythingAsync();
        _edge.CloseRequested += Shutdown;
        _edge.ClaudeClicked += () => _ = RenewClaudeSessionAsync();
        _edge.Show();

        _tray = new TrayIcon("EdgeWidget");
        _tray.Clicked += ShowTrayMenu;

        _warmupTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = WarmupDuration };
        _warmupTimer.Tick += (_, _) =>
        {
            _warmupTimer?.Stop();
            _warmingUp = false;
            UpdateCadence();
        };
        _warmupTimer.Start();

        UpdateCadence();
        _refreshTimer.Start();
        _sensorTimer.Start();
        _ = RefreshUsageAsync();
        _ = RefreshSensorsAsync();
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew) return true;
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and belongs to an elevated instance.
            return false;
        }
    }

    /// Claude and Codex (every 2 minutes). Joins a refresh already in flight instead of starting a second one.
    private Task RefreshUsageAsync()
    {
        if (_usageRefresh is null || _usageRefresh.IsCompleted) _usageRefresh = RunUsageRefreshAsync();
        return _usageRefresh;
    }

    private async Task RunUsageRefreshAsync()
    {
        if (_edge is null) return;
        try
        {
            Log.Trace("Usage", "refresh started");
            Task<ClaudeSnapshot> claude = _claude.FetchAsync();
            Task<CodexSnapshot> codex = _codex.FetchAsync();

            _lastClaude = await claude;
            _edge.SetClaude(_lastClaude);
            _lastCodex = await codex;
            _edge.SetCodex(_lastCodex);

            // Grok Bot is a file read, not a request: re-read it here so hand-edited figures show up.
            _lastGrok = _grok.Read();
            _edge.SetGrok(_lastGrok);

            _edge.ReassertTopmost();
            UpdateTooltip();
            Log.Trace("Usage", "refresh finished");
        }
        catch (Exception ex)
        {
            Log.Error("Usage refresh", ex);
        }
    }

    /// CPU and GPU, read together from the same LibreHardwareMonitor instance. Joins a read already in flight.
    private Task RefreshSensorsAsync()
    {
        if (_sensorRefresh is null || _sensorRefresh.IsCompleted) _sensorRefresh = RunSensorRefreshAsync();
        return _sensorRefresh;
    }

    private async Task RunSensorRefreshAsync()
    {
        if (_sensors is null || _edge is null) return;
        try
        {
            Log.Trace("Sensors", "sample");
            HardwareSnapshot snapshot = await _sensors.SampleAsync();
            _lastCpu = snapshot.Cpu;
            _lastGpu = snapshot.Gpu;
            _edge.SetCpu(snapshot.Cpu);
            _edge.SetGpu(snapshot.Gpu);
            UpdateTooltip();
        }
        catch (Exception ex)
        {
            Log.Error("Sensor refresh", ex);
        }
    }

    /// Panel "Actualizar" and tray "Actualizar ahora": CPU, GPU, Claude and Codex at once, joining any refresh already
    /// in flight. The panel button reads "Actualizando" and stays disabled until everything has finished.
    private async Task RefreshEverythingAsync()
    {
        if (_manualRefresh || _edge is null) return;
        _manualRefresh = true;
        _edge.SetRefreshing(true);
        Log.Trace("Refresh", "manual refresh started");
        var clock = Stopwatch.StartNew();
        try
        {
            // Restart the running periodic timers so an automatic refresh does not follow right after this one.
            RestartIfRunning(_refreshTimer);
            RestartIfRunning(_sensorTimer);
            await Task.WhenAll(RefreshUsageAsync(), RefreshSensorsAsync());
        }
        finally
        {
            _edge.SetRefreshing(false);
            _manualRefresh = false;
            Log.Trace("Refresh", $"manual refresh finished in {clock.ElapsedMilliseconds} ms");
        }
    }

    /// Clicking the Claude ring. The widget never touches the credentials file itself: it asks the scheduled
    /// task to do it, which runs as the plain user, refreshes the token and opens Claude Code. When that task
    /// is not installed, Claude Code is opened on its own — which does not renew anything, but at least puts
    /// the user where they can. The usage is read again once the refresh has had time to finish.
    private async Task RenewClaudeSessionAsync()
    {
        if (_renewing || _edge is null) return;
        _renewing = true;
        try
        {
            _edge.SetClaudeRenewing();
            Log.Info("Claude", "renovación de sesión pedida desde el anillo");

            if (!await Task.Run(RunRenewTask))
            {
                Log.Warn("Claude", $"la tarea «{RenewTaskName}» no está instalada; se abre Claude Code");
                Start("explorer.exe", ClaudeAppId);
            }

            // The CLI needs a few seconds to refresh the token and rewrite the credentials file.
            await Task.Delay(TimeSpan.FromSeconds(15));
            await RefreshUsageAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Claude renew", ex);
        }
        finally
        {
            _renewing = false;
        }
    }

    /// True when schtasks reported the task as started.
    private static bool RunRenewTask()
    {
        try
        {
            using Process? process = Start("schtasks.exe", $"/run /tn \"{RenewTaskName}\"");
            return process is not null && process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Claude", "schtasks: " + ex.Message);
            return false;
        }
    }

    private static Process? Start(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true });

    private static void RestartIfRunning(DispatcherTimer? timer)
    {
        if (timer is not { IsEnabled: true }) return;
        timer.Stop();
        timer.Start();
    }

    /// Fast cadence only while the pinned panel is on screen: 20 s for the sensors, 2 min for the usage APIs.
    /// Hidden (auto mode, or collapsed) it drops to 1 min and 6 min — the sensors keep running so the session
    /// maximum stays honest, but neither source costs anything worth measuring while nobody is looking.
    private void UpdateCadence()
    {
        bool visible = _panelMode == PanelMode.Pinned && _panelExpanded;
        SetInterval(_sensorTimer, _warmingUp ? SensorWarmupInterval
            : visible ? SensorVisibleInterval : SensorHiddenInterval);
        SetInterval(_refreshTimer, visible ? UsageVisibleInterval : UsageHiddenInterval);
    }

    /// Writing Interval restarts a running timer, so only write it when the cadence actually changes.
    private static void SetInterval(DispatcherTimer? timer, TimeSpan interval)
    {
        if (timer is null || timer.Interval == interval) return;
        timer.Interval = interval;
        Log.Trace("Cadence", $"{interval.TotalSeconds:0} s");
    }

    private void UpdateTooltip()
    {
        string claude = _lastClaude?.Session is UsageWindow s ? Fmt.Percent(s.Percent) : "--";
        string cpu = _lastCpu?.Temperature is double t ? Fmt.Celsius(t) : "--";
        string codex = _lastCodex is null || _lastCodex.Hidden ? string.Empty
            : $" · Codex {(_lastCodex.Primary is CodexWindow w ? Fmt.Percent(w.Percent) : "--")}";
        string gpu = _lastGpu is null || !_lastGpu.Detected ? string.Empty
            : $" · GPU {(_lastGpu.Temperature is double g ? Fmt.Celsius(g) : "--")}";
        _tray?.SetTooltip($"Claude {claude}{codex} · CPU {cpu}{gpu}");
        LogStatusChange();
    }

    /// One log line whenever a source changes between available and failing (not on every refresh).
    private void LogStatusChange()
    {
        string claude = _lastClaude is null ? "pendiente" : _lastClaude.Session is not null ? "ok" : _lastClaude.Message ?? "sin datos";
        string codex = _lastCodex is null ? "pendiente"
            : _lastCodex.Hidden ? $"oculto ({_lastCodex.Message})"
            : _lastCodex.Primary is not null ? "ok" : _lastCodex.Message ?? "sin datos";
        string grok = _lastGrok is null ? "pendiente"
            : _lastGrok.Hidden ? "sin configurar"
            : _lastGrok.Weekly is not null ? "ok" : _lastGrok.Message ?? "sin datos";
        string cpu = _lastCpu is null ? "pendiente" : _lastCpu.Temperature is not null ? "ok" : _lastCpu.Message ?? "sin datos";
        string gpu = _lastGpu is null ? "pendiente"
            : !_lastGpu.Detected ? "no detectada"
            : _lastGpu.Temperature is not null ? "ok" : _lastGpu.Message ?? "sin datos";
        string status = $"Claude: {claude} | Codex: {codex} | Grok: {grok} | CPU temperatura: {cpu} | GPU temperatura: {gpu}";
        if (status == _lastStatus) return;
        _lastStatus = status;
        Log.Info("Status", status);
    }

    /// The mode lives here: persisted to EdgeWidget.settings.json, then applied to the panel.
    private void SetPanelMode(PanelMode mode)
    {
        if (_panelMode == mode || _edge is null) return;
        _panelMode = mode;
        SettingsStore.SavePanelMode(mode);
        _edge.ApplyMode(mode);
        UpdateCadence();
    }

    private void ShowTrayMenu()
    {
        _menu?.CloseMenu();

        var menu = new TrayMenuWindow();
        menu.RefreshRequested += () => _ = RefreshEverythingAsync();
        menu.ExitRequested += Shutdown;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };
        _menu = menu;
        menu.ShowAtCursor();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _refreshTimer?.Stop();
        _sensorTimer?.Stop();
        _warmupTimer?.Stop();
        _tray?.Dispose();
        _sensors?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _mutex.Dispose();
        }
        Log.Info("App", "exit");
        base.OnExit(e);
    }
}
