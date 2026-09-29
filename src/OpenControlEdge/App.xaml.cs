using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using OpenControlEdge.Interop;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;
using OpenControlEdge.Views;

namespace OpenControlEdge;

public partial class App : Application
{
    private const string MutexName = @"Local\OpenControlEdge.SingleInstance.7F3C2A1E";

    /// Background renewal of the Claude session: considered once the token has less than AutoRenewWindow left,
    /// run AutoRenewLead before the expiry (inside the CLI's own 5-minute refresh window, so one run is enough) and
    /// at most once every AutoRenewInterval.
    private static readonly TimeSpan AutoRenewWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AutoRenewLead = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan AutoRenewInterval = TimeSpan.FromMinutes(30);

    /// How long the outcome of a renewal stays on the Claude card.
    private static readonly TimeSpan ClaudeNoteDuration = TimeSpan.FromMinutes(2);

    /// "Liberar RAM" at most once a minute.
    private static readonly TimeSpan RamCleanInterval = TimeSpan.FromMinutes(1);

    // Two cadences for each source: the fast one only while the pinned panel is actually on screen, the slow
    // one the rest of the time. The sensors never stop, so "Máxima de la sesión" also catches the peaks
    // nobody was watching — the boot spike above all.
    private static readonly TimeSpan SensorVisibleInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SensorHiddenInterval = TimeSpan.FromMinutes(1);

    /// The widget starts with the session, right inside the burst of programs that makes this laptop run hot.
    /// For the first few minutes the sensors are read often enough to catch that peak whatever the panel is doing.
    private static readonly TimeSpan SensorWarmupInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromMinutes(3);

    private readonly ClaudeUsageService _claude = new();
    private readonly CodexUsageService _codex = new();
    private readonly CursorUsageService _cursor = new();
    private readonly OpenCodeUsageService _openCode = new();
    private readonly DeepSeekUsageService _deepSeek = new();
    private readonly OpenRouterUsageService _openRouter = new();
    private Mutex? _mutex;
    private HardwareSensorService? _sensors;
    private EdgeWindow? _edge;
    private TrayIcon? _tray;
    private TrayMenuWindow? _menu;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _sensorTimer;
    private DispatcherTimer? _warmupTimer;
    private DispatcherTimer? _autoUpdateTimer;
    private bool _warmingUp = true;
    private Task? _usageRefresh;
    private Task? _sensorRefresh;
    private ClaudeSnapshot? _lastClaude;
    private CodexSnapshot? _lastCodex;
    private CursorSnapshot? _lastCursor;
    private OpenCodeSnapshot? _lastOpenCode;
    private DeepSeekSnapshot? _lastDeepSeek;
    private OpenRouterSnapshot? _lastOpenRouter;
    private CpuSnapshot? _lastCpu;
    private GpuSnapshot? _lastGpu;
    private RamSnapshot? _lastRam;
    private string? _lastStatus;
    private PanelMode _panelMode;
    private bool _panelExpanded;
    private bool _manualRefresh;
    private bool _renewing;
    private DispatcherTimer? _autoRenewTimer;
    private DispatcherTimer? _claudeNoteTimer;
    private DateTime _lastAutoRenew = DateTime.MinValue;
    private DateTime _lastRamClean = DateTime.MinValue;
    private bool _cleaningRam;
    private bool _firstDataLogged;
    private SettingsWindow? _settingsWindow;
    private readonly Dictionary<AiProviderId, (string Message, DateTimeOffset At)> _agentFailures = new();
    private DateTimeOffset? _lastUsageRefresh;
#if DEBUG
    private bool _startupException;
#endif
    private bool _agentsProbed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Dispatcher", args.Exception);
#if DEBUG
            _startupException = true;
#endif
            args.Handled = true;
        };


        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) {
#if DEBUG
                _startupException = true;
#endif
                Log.Error("AppDomain", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
#if DEBUG
            _startupException = true;
#endif
            Log.Error("Task", args.Exception);
            args.SetObserved();
        };

        int updateHelperArg = Array.IndexOf(e.Args, "--apply-update");
        if (updateHelperArg >= 0 && updateHelperArg + 2 < e.Args.Length)
        {
            int.TryParse(e.Args[updateHelperArg + 2], out int parentPid);
            Environment.ExitCode = UpdateInstaller.ApplyAfterParentExit(e.Args[updateHelperArg + 1], parentPid) ? 0 : 1;
            Shutdown(Environment.ExitCode);
            return;
        }

#if DEBUG
        int snapshotArg = Array.IndexOf(e.Args, "--snapshot");
        if (snapshotArg >= 0 && snapshotArg + 1 < e.Args.Length)
        {
            Log.Suppress();
            try { Snapshot.Run(e.Args[snapshotArg + 1]); Shutdown(0); }
            catch (Exception) { Environment.ExitCode = 1; Shutdown(1); }
            return;
        }
#endif

#if DEBUG
        int fixtureArg = Array.IndexOf(e.Args, "--test-update-fixture");
        if (fixtureArg >= 0 && fixtureArg + 2 < e.Args.Length)
        {
            try { Environment.ExitCode = RunUpdateFixtureAsync(e.Args[fixtureArg + 1], e.Args[fixtureArg + 2]).GetAwaiter().GetResult() ? 0 : 1; }
            catch (Exception ex) { Environment.ExitCode = 1; Log.Error("Update fixture", ex); }
            Shutdown(Environment.ExitCode);
            return;
        }
#endif

        // Theme, ring colours and language before any window exists, so nothing paints twice.
        Settings appearance = SettingsStore.Load();
        ThemeManager.Apply(appearance.Theme, appearance.ColorTheme);
        Loc.Apply(appearance.Language);

        if (Array.IndexOf(e.Args, "--check-pawnio") >= 0)
        {
            bool installed;
            try { installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch { installed = false; }
            Environment.ExitCode = installed ? 0 : 1;
            Shutdown(Environment.ExitCode);
            return;
        }


        // Run from anywhere but C:\Program Files\OpenControlEdge (the unzipped release): offer to install. Before the
        // single-instance check, so a copy already running can be replaced.
        if (ShouldOfferInstall(e.Args))
        {
            var welcome = new InstallWindow(InstallWindow.Mode.Install);
            welcome.ShowDialog();
            if (welcome.Result != InstallWindow.Outcome.Portable)
            {
                Shutdown();
                return;
            }
        }

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        _panelMode = SettingsStore.Load().PanelMode;
        Log.Info("App", $"started (panel {_panelMode}, {(UnelevatedLauncher.IsElevated ? "elevated" : "not elevated")})");

        // Timers exist before the window is shown: a pinned panel expands during Show() and sets the cadence.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(SettingsStore.Load().UsageRefreshMinutes) };
        _refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        _sensorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SensorHiddenInterval };
        _sensorTimer.Tick += async (_, _) => await RefreshSensorsAsync();

        _sensors = new HardwareSensorService();
        _edge = new EdgeWindow(_sensors.SessionStart, _panelMode);
        Settings settings = SettingsStore.Load();
        _edge.ApplyScale(settings.UiScale);

        // The last reading, painted before any network request (UsageCache); the first refresh replaces it.
        UsageCache.Cached cached = UsageCache.Load(DateTimeOffset.Now);
        _lastClaude = AiRingPolicy.ShouldShowRing(AiProviderId.Claude, settings)
            ? cached.Claude ?? ClaudeSnapshot.Failed("Cargando…") : ClaudeSnapshot.Absent();
        _edge.SetClaude(_lastClaude);
        _edge.ApplyUsageView(settings.UsageView);
        _lastCodex = InitialCodex(settings, cached.Codex);
        _edge.SetCodex(_lastCodex);
        _lastCursor = InitialCursor(settings, cached.Cursor);
        _edge.SetCursor(_lastCursor);
        _lastRam = MemoryService.Read();
        _edge.SetRam(_lastRam);
        _lastOpenCode = InitialOpenCode(settings);
        _edge.SetOpenCode(_lastOpenCode);
        _lastDeepSeek = InitialDeepSeek(settings);
        _edge.SetDeepSeek(_lastDeepSeek);
        _lastOpenRouter = InitialOpenRouter(settings);
        _edge.SetOpenRouter(_lastOpenRouter);
        _edge.ExpandedChanged += expanded =>
        {
            _panelExpanded = expanded;
            // A fresh reading the moment the panel opens, so a hover never shows a minute-old temperature.
            if (expanded) _ = RefreshSensorsAsync();
            UpdateCadence();
        };
        _edge.ModeChangeRequested += SetPanelMode;
        _edge.UsageViewChanged += SettingsStore.SaveUsageView;
        _edge.SettingsRequested += OpenSettings;
        _edge.CloseRequested += Shutdown;
        _edge.ClaudeClicked += () => _ = RenewClaudeSessionAsync(automatic: false);
        _edge.CpuClicked += OpenSystemInformation;
        _edge.RamClicked += () => _ = FreeRamAsync();
        bool fromCache = cached.Claude is not null || cached.Codex is not null || cached.Cursor is not null;
        _edge.ContentRendered += (_, _) => LogStartup(fromCache ? "primer dibujo con datos de la caché" : "primer dibujo");
        _edge.ContentRendered += (_, _) =>
        {
#if DEBUG
            if (Array.IndexOf(e.Args, "--smoke-test") < 0) return;
            OpenSettings();
            var smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            smokeTimer.Tick += (_, _) =>
            {
                smokeTimer.Stop();
                bool opened = _edge.IsVisible && _settingsWindow?.IsVisible == true;
                Log.Info("SmokeTest", $"main={_edge.IsVisible}, settings={_settingsWindow?.IsVisible}, dispatcherException={_startupException}");
                Environment.ExitCode = opened && !_startupException ? 0 : 1;
                Shutdown(Environment.ExitCode);
            };
            smokeTimer.Start();
#endif
        };
        _edge.Show();

        _tray = new TrayIcon("Open Control Edge");
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
        StartAutomaticUpdateCheck();
        ConfigureAutoUpdateTimer(SettingsStore.Load());
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        OpenSettings("General");
    }

    private void OpenSettings(string category, UpdateRelease? release = null)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            if (release is not null) _settingsWindow.ShowUpdateResult(release);
            else _settingsWindow.Activate();
            return;
        }
        Settings previousSettings = SettingsStore.Load();
        _settingsWindow = new SettingsWindow(() => RefreshUsageAsync(), settings =>
        {
            _edge?.ApplyScale(settings.UiScale);
            if (_panelMode != settings.PanelMode) SetPanelMode(settings.PanelMode);
            SetInterval(_refreshTimer, TimeSpan.FromMinutes(settings.UsageRefreshMinutes));
            ApplyCurrentSettings(settings);
            ConfigureAutoUpdateTimer(settings);
            bool updateCheckEnabled = !previousSettings.AutoCheckUpdates && settings.AutoCheckUpdates;
            if (updateCheckEnabled) StartAutomaticUpdateCheck();
            bool providersChanged = !previousSettings.Providers.OrderBy(x => x.Key).SequenceEqual(settings.Providers.OrderBy(x => x.Key));
            bool refreshChanged = previousSettings.UsageRefreshMinutes != settings.UsageRefreshMinutes;
            previousSettings = settings;
            if (providersChanged || refreshChanged) _ = RefreshUsageAsync();
        }, AgentStatus, RetryProviderAsync, category, release, () => _lastUsageRefresh,
            () => _lastCpu?.Temperature is not null || _lastGpu?.Temperature is not null) { Owner = _edge };
        _settingsWindow.ContentRendered += (_, _) => Log.Info("Settings", "settings window content rendered");
        if (category == "Agentes" && !_agentsProbed)
        {
            _agentsProbed = true;
            _ = ProbeAgentsAsync(_settingsWindow);
        }
        _settingsWindow.Show();
    }

    private async Task ProbeAgentsAsync(SettingsWindow window)
    {
        await Task.WhenAll(AiDetector.All.Select(RetryProviderAsync));
        if (window.IsVisible) window.RefreshAgents();
    }

    private async void StartAutomaticUpdateCheck()
    {
        Settings settings = SettingsStore.Load();
        if (!settings.AutoCheckUpdates || settings.LastAutoUpdateCheck is DateTimeOffset previous && DateTimeOffset.Now - previous < TimeSpan.FromHours(24)) return;
        SettingsStore.Update(s => s with { LastAutoUpdateCheck = DateTimeOffset.Now });
        UpdateCheckResult result = await UpdateService.CheckAsync();
        if (result.Release is not null)
            _ = Dispatcher.BeginInvoke(() => OpenSettings("Actualizaciones", result.Release));
        else if (result.Error is not null) Log.Warn("Updates", result.Error);
    }

    private void ConfigureAutoUpdateTimer(Settings settings)
    {
        if (!settings.AutoCheckUpdates)
        {
            _autoUpdateTimer?.Stop();
            return;
        }
        _autoUpdateTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(1) };
        _autoUpdateTimer.Tick -= OnAutoUpdateTimer;
        _autoUpdateTimer.Tick += OnAutoUpdateTimer;
        if (!_autoUpdateTimer.IsEnabled) _autoUpdateTimer.Start();
    }

    private void OnAutoUpdateTimer(object? sender, EventArgs e) => StartAutomaticUpdateCheck();

    private async Task<bool> RunUpdateFixtureAsync(string endpoint, string resultPath)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback || uri.Scheme != "http") return false;
        UpdateCheckResult result = await UpdateService.CheckAsync(endpoint).ConfigureAwait(false);
        if (result.Release is null) throw new InvalidDataException(result.Error ?? "fixture returned no newer release");
        string stage = Path.Combine(AppContext.BaseDirectory, "update-fixture-stage");
        var tampered = result.Release with { Digest = new string('0', 64) };
        try
        {
            await UpdateService.DownloadAndStageAsync(tampered, stage).ConfigureAwait(false);
            throw new InvalidOperationException("tampered archive digest was accepted");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("digest", StringComparison.OrdinalIgnoreCase)) { }
        if (Directory.Exists(stage)) throw new InvalidDataException("tampered archive created a staging folder");
        var endpointUri = new Uri(endpoint);
        string noDigestApi = endpointUri.GetLeftPart(UriPartial.Authority) + "/nodigest";
        UpdateCheckResult noDigest = await UpdateService.CheckAsync(noDigestApi).ConfigureAwait(false);
        if (noDigest.Release is not null || noDigest.Error is null) throw new InvalidDataException("a release without digest was accepted");
        string payload = await UpdateService.DownloadAndStageAsync(result.Release, stage).ConfigureAwait(false);
        string marker = Path.Combine(payload, "fixture.marker");
        if (!File.Exists(Path.Combine(payload, "OpenControlEdge.exe")) || !File.Exists(marker))
            throw new InvalidDataException("fixture archive contents were not staged");
        string target = Path.Combine(AppContext.BaseDirectory, "update-fixture-target");
        string old = Path.Combine(target, "old.marker");
        string backup = target + ".fixture-backup";
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.CreateDirectory(target); await File.WriteAllTextAsync(old, "old");
            UpdateInstaller.SwapDirectory(payload, target, backup);
            if (!File.Exists(Path.Combine(target, "fixture.marker"))) throw new InvalidDataException("fixture directory swap failed");
            // Force the failure branch with a missing payload and ensure rollback restores the previous directory.
            string missing = Path.Combine(stage, "missing-payload");
            bool rolledBack = !UpdateInstaller.SwapDirectory(missing, target, backup, failAfterBackup: true)
                && File.Exists(Path.Combine(target, "fixture.marker"));
            if (!rolledBack) throw new InvalidDataException("fixture rollback failed");
            await File.WriteAllTextAsync(resultPath, $"OK {result.Release.Tag}; digest verified; missing and invalid digests rejected; extraction, swap and rollback verified").ConfigureAwait(false);
            return true;
        }
        finally
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch { }
            try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
        }
    }

    private void ApplyCurrentSettings(Settings settings)
    {
        _edge?.ApplyUsageView(settings.UsageView);
        _edge?.ApplyScale(settings.UiScale);
        _panelMode = settings.PanelMode;
        UpdateCadence();
    }

    private async Task RetryProviderAsync(AiProviderId id)
    {
        if (!AiDetector.IsInstalled(id)) return;
        Settings settings = SettingsStore.Load();
        switch (id)
        {
            case AiProviderId.Claude: _lastClaude = await FetchClaudePreservingAsync(); TrackAgent(id, _lastClaude.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetClaude(_lastClaude); break;
            case AiProviderId.Codex: _lastCodex = await _codex.FetchAsync(); TrackAgent(id, _lastCodex.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetCodex(_lastCodex); break;
            case AiProviderId.Cursor: _lastCursor = await _cursor.FetchAsync(); TrackAgent(id, _lastCursor.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetCursor(_lastCursor); break;
            case AiProviderId.OpenCode: _lastOpenCode = await _openCode.FetchAsync(); TrackAgent(id, _lastOpenCode.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetOpenCode(_lastOpenCode); break;
            case AiProviderId.DeepSeek: _lastDeepSeek = await _deepSeek.FetchAsync(); TrackAgent(id, _lastDeepSeek.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetDeepSeek(_lastDeepSeek); break;
            case AiProviderId.OpenRouter: _lastOpenRouter = await _openRouter.FetchAsync(); TrackAgent(id, _lastOpenRouter.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetOpenRouter(_lastOpenRouter); break;
        }
        UpdateTooltip();
    }

    private AgentStatus AgentStatus(AiProviderId id)
    {
        if (!AiDetector.IsInstalled(id)) return new("missing", null, null, null);
        (string? message, string? plan) = id switch
        {
            AiProviderId.Claude => (_lastClaude?.Message, _lastClaude?.Plan),
            AiProviderId.Codex => (_lastCodex?.Message, _lastCodex?.Plan),
            AiProviderId.Cursor => (_lastCursor?.Message, _lastCursor?.Plan),
            AiProviderId.OpenCode => (_lastOpenCode?.Message, null),
            AiProviderId.DeepSeek => (_lastDeepSeek?.Message, null),
            AiProviderId.OpenRouter => (_lastOpenRouter?.Message, null),
            _ => (null, null),
        };
        if (id == AiProviderId.Claude && _lastClaude?.Session is not null
            || id == AiProviderId.Codex && _lastCodex?.Primary is not null
            || id == AiProviderId.Cursor && _lastCursor?.Cycle is not null
            || id == AiProviderId.OpenCode && _lastOpenCode is { Message: null }
            || id == AiProviderId.DeepSeek && _lastDeepSeek is { Balance: not null }
            || id == AiProviderId.OpenRouter && _lastOpenRouter is { Message: null })
            return new("connected", plan, null, null);
        bool noSession = message is not null && (message.Contains("sesi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || message.Contains("inicia", StringComparison.OrdinalIgnoreCase)
            || message.Contains("clave API", StringComparison.OrdinalIgnoreCase));
        return new(noSession ? "session" : "error", plan, message ?? "Sin respuesta",
            _agentFailures.TryGetValue(id, out var failure) ? failure.At : null);
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew) return true;
            Log.Warn("App", "no se inició porque otra instancia ya ocupa el mutex de instancia única");
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and belongs to an elevated instance.
            Log.Warn("App", "no se pudo adquirir el mutex de instancia única");
            return false;
        }
    }

    /// Refreshes all usage providers concurrently. Joins an in-flight refresh.
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
            Settings settings = SettingsStore.Load();

            await Task.WhenAll(
                ApplyClaudeAsync(settings), ApplyCodexAsync(settings), ApplyCursorAsync(settings),
                ApplyOpenCodeAsync(settings), ApplyDeepSeekAsync(settings), ApplyOpenRouterAsync(settings));

            _edge.ReassertTopmost();
            UpdateTooltip();
            if (!_firstDataLogged)
            {
                _firstDataLogged = true;
                LogStartup("primera lectura de red pintada");
            }
            UsageCache.Save(_lastClaude, _lastCodex, _lastCursor, DateTimeOffset.Now);
            _lastUsageRefresh = DateTimeOffset.Now;
            ArmAutoRenew();
            Log.Trace("Usage", "refresh finished");
        }
        catch (Exception ex)
        {
            Log.Error("Usage refresh", ex);
        }
    }

    /// Time since the process started, in the log: how fast the widget shows data (cold and warm start measurements).
    private static void LogStartup(string what)
    {
        using var process = Process.GetCurrentProcess();
        Log.Info("Startup", $"{what}: {(DateTime.Now - process.StartTime).TotalMilliseconds:0} ms desde el inicio del proceso, "
                            + $"{process.WorkingSet64 / (1024 * 1024)} MB de memoria");
    }

    private void TrackAgent(AiProviderId id, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) { _agentFailures.Remove(id); return; }
        if (!_agentFailures.TryGetValue(id, out var existing) || existing.Message != message)
            _agentFailures[id] = (message, DateTimeOffset.Now);
    }
    private async Task ApplyClaudeAsync(Settings s) { if (_renewing) return; _lastClaude = await RefreshClaudeAsync(s); TrackAgent(AiProviderId.Claude, _lastClaude.Message); _edge?.SetClaude(_lastClaude); }
    private async Task ApplyCodexAsync(Settings s) { _lastCodex = await RefreshCodexAsync(s); TrackAgent(AiProviderId.Codex, _lastCodex.Message); _edge?.SetCodex(_lastCodex); }
    private async Task ApplyCursorAsync(Settings s) { _lastCursor = await RefreshCursorAsync(s); TrackAgent(AiProviderId.Cursor, _lastCursor.Message); _edge?.SetCursor(_lastCursor); }
    private async Task ApplyOpenCodeAsync(Settings s) { _lastOpenCode = await RefreshOpenCodeAsync(s); TrackAgent(AiProviderId.OpenCode, _lastOpenCode.Message); _edge?.SetOpenCode(_lastOpenCode); }
    private async Task ApplyDeepSeekAsync(Settings s) { _lastDeepSeek = await RefreshDeepSeekAsync(s); TrackAgent(AiProviderId.DeepSeek, _lastDeepSeek.Message); _edge?.SetDeepSeek(_lastDeepSeek); }
    private async Task ApplyOpenRouterAsync(Settings s) { _lastOpenRouter = await RefreshOpenRouterAsync(s); TrackAgent(AiProviderId.OpenRouter, _lastOpenRouter.Message); _edge?.SetOpenRouter(_lastOpenRouter); }

    /// The cached reading when there is a usable login (no network), otherwise what Initial says.
    private CodexSnapshot InitialCodex(Settings settings, CodexSnapshot? cached)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.Codex, settings)) return CodexSnapshot.Absent();
        if (!AiDetector.IsInstalled(AiProviderId.Codex)) return CodexSnapshot.NotAvailable(AiDetector.CodexLoginMessage);
        CodexSnapshot initial = _codex.Initial();
        return initial.Message == "Cargando…" && cached is not null ? cached : initial;
    }

    private CursorSnapshot InitialCursor(Settings settings, CursorSnapshot? cached)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.Cursor, settings)) return CursorSnapshot.Absent();
        if (!AiDetector.IsInstalled(AiProviderId.Cursor)) return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage);
        return cached ?? _cursor.Initial();
    }

    private OpenCodeSnapshot InitialOpenCode(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.OpenCode, settings)) return OpenCodeSnapshot.Absent();
        return AiDetector.IsInstalled(AiProviderId.OpenCode) ? OpenCodeSnapshot.Failed("Cargando…")
            : OpenCodeSnapshot.Failed("Instala OpenCode para activar este anillo");
    }

    private DeepSeekSnapshot InitialDeepSeek(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.DeepSeek, settings)) return DeepSeekSnapshot.Absent();
        return AiDetector.IsInstalled(AiProviderId.DeepSeek) ? DeepSeekSnapshot.Failed("Cargando…")
            : DeepSeekSnapshot.Failed("Añade la clave API desde Claves de API…");
    }

    private OpenRouterSnapshot InitialOpenRouter(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.OpenRouter, settings)) return OpenRouterSnapshot.Absent();
        return AiDetector.IsInstalled(AiProviderId.OpenRouter) ? OpenRouterSnapshot.Failed("Cargando…")
            : OpenRouterSnapshot.Failed("Añade la clave API desde Claves de API…");
    }

    private async Task<ClaudeSnapshot> RefreshClaudeAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.Claude, settings)) return ClaudeSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.Claude, settings))
            return ClaudeSnapshot.Failed(AiDetector.ClaudeLoginMessage);
        return await FetchClaudePreservingAsync();
    }

    private async Task<ClaudeSnapshot> FetchClaudePreservingAsync()
    {
        if (_claude.RetryAfterUntil is DateTimeOffset retryAt && DateTimeOffset.UtcNow < retryAt
            && _lastClaude is { Hidden: false, Session: not null })
            return _lastClaude;
        ClaudeSnapshot current = await _claude.FetchAsync();
        if (current.Message is string error && error.StartsWith("Error HTTP ", StringComparison.Ordinal)
            && int.TryParse(error.AsSpan("Error HTTP ".Length), out int status)
            && (status == 429 || status >= 500)
            && _lastClaude is { Hidden: false, Session: not null } previous)
            return previous with { Plan = current.Plan ?? previous.Plan, TokenExpiresAt = current.TokenExpiresAt };
        return current;
    }

    private async Task<CodexSnapshot> RefreshCodexAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.Codex, settings)) return CodexSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.Codex, settings))
            return CodexSnapshot.NotAvailable(AiDetector.CodexLoginMessage);
        return await _codex.FetchAsync();
    }

    private async Task<CursorSnapshot> RefreshCursorAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.Cursor, settings)) return CursorSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.Cursor, settings))
            return CursorSnapshot.NotAvailable(AiDetector.CursorLoginMessage);
        return await _cursor.FetchAsync();
    }

    private async Task<OpenCodeSnapshot> RefreshOpenCodeAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.OpenCode, settings)) return OpenCodeSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.OpenCode, settings)) return OpenCodeSnapshot.Failed("No se detecta OpenCode");
        return await _openCode.FetchAsync();
    }

    private async Task<DeepSeekSnapshot> RefreshDeepSeekAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.DeepSeek, settings)) return DeepSeekSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.DeepSeek, settings)) return DeepSeekSnapshot.Failed("Añade la clave API desde Claves de API…");
        return await _deepSeek.FetchAsync();
    }

    private async Task<OpenRouterSnapshot> RefreshOpenRouterAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.OpenRouter, settings)) return OpenRouterSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.OpenRouter, settings)) return OpenRouterSnapshot.Failed("Añade la clave API desde Claves de API…");
        return await _openRouter.FetchAsync();
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
            _lastRam = MemoryService.Read();
            _edge.SetRam(_lastRam);
            UpdateTooltip();
        }
        catch (Exception ex)
        {
            Log.Error("Sensor refresh", ex);
        }
    }

    /// Tray "Actualizar ahora": refresh usage providers and sensors, joining in-flight work.
    private async Task RefreshEverythingAsync()
    {
        if (_manualRefresh || _edge is null) return;
        _manualRefresh = true;
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
            _manualRefresh = false;
            Log.Trace("Refresh", $"manual refresh finished in {clock.ElapsedMilliseconds} ms");
        }
    }

    /// Renews the Claude session with no window at all: the Claude Code CLI runs once, hidden, as the plain user
    /// (ClaudeSessionRenewer, UnelevatedLauncher), then Claude usage is read again. Clicked: the card goes
    /// "Renovando sesión…" → the new reading → a line with the outcome. Automatic: nothing on screen but the new
    /// reading. The widget itself never writes the credentials file.
    private async Task RenewClaudeSessionAsync(bool automatic)
    {
        if (_renewing || _edge is null) return;
        _renewing = true;
        try
        {
            if (!automatic) _edge.SetClaudeRenewing();
            Log.Info("Claude", automatic ? "renovación automática de la sesión" : "renovación de sesión pedida desde el anillo");

            RenewResult result = await ClaudeSessionRenewer.RenewAsync();
            Log.Info("Claude", $"renovación: {result.Outcome}");

            _renewing = false;
            await ApplyClaudeAsync(SettingsStore.Load());
            UpdateTooltip();
            ArmAutoRenew();
            if (!automatic) ShowClaudeNote(result);
        }
        catch (Exception ex)
        {
            Log.Error("Claude renew", ex);
            _renewing = false;
            if (_lastClaude is not null) _edge.SetClaude(_lastClaude);
            _edge.SetClaudeNote(Loc.Message("No se pudo renovar la sesión"));
        }
        finally
        {
            _renewing = false;
        }
    }

    /// The outcome of a clicked renewal under the Claude card, for ClaudeNoteDuration.
    private void ShowClaudeNote(RenewResult result)
    {
        if (_edge is null) return;
        DateTime? until = result.ExpiresAt?.LocalDateTime;
        string note = result.Outcome switch
        {
            RenewOutcome.Renewed when until is DateTime at => Loc.Format("Claude.Renewed", at),
            RenewOutcome.StillValid when until is DateTime at => Loc.Format("Claude.StillValid", at),
            RenewOutcome.CliMissing => Loc.Message(ClaudeSessionRenewer.CliMissingMessage)!,
            RenewOutcome.TimedOut => Loc.Message(ClaudeSessionRenewer.TimedOutMessage)!,
            RenewOutcome.Failed => Loc.Message(ClaudeSessionRenewer.StartFailedMessage)!,
            _ => Loc.Message(ClaudeSessionRenewer.NotRenewedMessage)!,
        };
        _edge.SetClaudeNote(note);

        _claudeNoteTimer ??= CreateOneShot(() => _edge?.SetClaudeNote(null));
        _claudeNoteTimer.Stop();
        _claudeNoteTimer.Interval = ClaudeNoteDuration;
        _claudeNoteTimer.Start();
    }

    /// Schedules the background renewal for AutoRenewLead before the token expires (right away if that moment has
    /// passed). Re-armed after every Claude reading, so a token renewed elsewhere simply moves the time.
    private void ArmAutoRenew()
    {
        _autoRenewTimer?.Stop();
        if (_lastClaude is not { Hidden: false, TokenExpiresAt: DateTimeOffset expiry }) return;
        if (!SettingsStore.Load().AutoRenewClaude) return;

        TimeSpan due = expiry - AutoRenewLead - DateTimeOffset.UtcNow;
        _autoRenewTimer ??= CreateOneShot(() => _ = AutoRenewAsync());
        _autoRenewTimer.Interval = due > TimeSpan.FromSeconds(1) ? due : TimeSpan.FromSeconds(1);
        _autoRenewTimer.Start();
    }

    private async Task AutoRenewAsync()
    {
        if (!SettingsStore.Load().AutoRenewClaude) return;
        if (ClaudeSessionRenewer.ReadExpiry() is not DateTimeOffset expiry || expiry - DateTimeOffset.UtcNow >= AutoRenewWindow) return;
        if (DateTime.UtcNow - _lastAutoRenew < AutoRenewInterval || !ClaudeSessionRenewer.IsAvailable) return;
        _lastAutoRenew = DateTime.UtcNow;
        await RenewClaudeSessionAsync(automatic: true);
    }

    private static DispatcherTimer CreateOneShot(Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    /// Clicking the CPU ring: Settings > System > About, opened as the plain user (never with this process's rights).
    private static void OpenSystemInformation()
    {
        string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Task.Run(() =>
        {
            UnelevatedLauncher.Result result = UnelevatedLauncher.Run(explorer, "ms-settings:about", null, hidden: false, wait: null);
            if (!result.Started) Log.Warn("CPU", "no se pudo abrir Información del sistema: " + result.Error);
        });
    }

    /// Clicking the RAM ring: a one-off clean-up, at most once a minute, unless turned off in the settings.
    private async Task FreeRamAsync()
    {
        if (_edge is null || _cleaningRam) return;
        if (!SettingsStore.Load().RamCleanup)
        {
            _edge.SetRamNote(Loc.Get("Ram.Disabled"));
            return;
        }
        TimeSpan since = DateTime.UtcNow - _lastRamClean;
        if (since < RamCleanInterval)
        {
            _edge.SetRamNote(Loc.Format("Ram.Wait", (int)Math.Ceiling((RamCleanInterval - since).TotalSeconds)));
            return;
        }

        _cleaningRam = true;
        _lastRamClean = DateTime.UtcNow;
        try
        {
            _edge.SetRamNote(Loc.Get("Value.RamFreeing"));
            RamCleanResult result = await MemoryService.CleanAsync();
            _lastRam = MemoryService.Read();
            _edge.SetRam(_lastRam);
            string freed = (result.FreedBytes / (1024 * 1024)).ToString("N0", Loc.Culture);
            string cache = (result.CacheFreedBytes / (1024 * 1024)).ToString("N0", Loc.Culture);
            _edge.SetRamNote(result.StandbyPurged ? Loc.Format("Ram.Freed", freed, cache) : Loc.Format("Ram.FreedNoCache", freed));
            UpdateTooltip();
        }
        catch (Exception ex)
        {
            Log.Error("RAM", ex);
            _edge.SetRamNote(null);
        }
        finally
        {
            _cleaningRam = false;
        }
    }

    /// The welcome window: only for a release build running outside the install folder, or with --welcome.
    /// --portable skips it (a copy run on purpose from its own folder, and the start-up measurements).
    private static bool ShouldOfferInstall(string[] args)
    {
#if DEBUG
        return false;
#else
        bool portableRequest = Array.IndexOf(args, "--no-elevate") >= 0 || Array.IndexOf(args, "--portable") >= 0;
        if (portableRequest && !UnelevatedLauncher.IsElevated) return false;
        if (Array.IndexOf(args, "--welcome") >= 0) return true;
        return !Installer.IsInstalledCopy;
#endif
    }

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
        SetInterval(_refreshTimer, TimeSpan.FromMinutes(SettingsStore.Load().UsageRefreshMinutes));
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
        string claude = _lastClaude is null || _lastClaude.Hidden ? string.Empty
            : _lastClaude.Session is UsageWindow s ? Fmt.Percent(s.Percent) : "--";
        string cpu = _lastCpu?.Temperature is double t ? Fmt.Celsius(t) : "--";
        string codex = _lastCodex is null || _lastCodex.Hidden ? string.Empty
            : $" · Codex {(_lastCodex.Primary is CodexWindow w ? Fmt.Percent(w.Percent) : "--")}";
        string cursor = _lastCursor is null || _lastCursor.Hidden ? string.Empty
            : $" · Cursor {(_lastCursor.Cycle is UsageWindow c ? Fmt.Percent(c.Percent) : "--")}";
        string gpu = _lastGpu is null || !_lastGpu.Detected ? string.Empty
            : $" · GPU {(_lastGpu.Temperature is double g ? Fmt.Celsius(g) : "--")}";
        gpu += _lastRam is { Message: null } ram ? $" · RAM {Fmt.Percent(ram.Percent)}" : string.Empty;
        string openCode = _lastOpenCode is { Hidden: false, Message: null } oc
            ? $" · OpenCode {CompactTokens((decimal)oc.TokensIn + oc.TokensOut + oc.TokensReasoning + oc.TokensCacheRead + oc.TokensCacheWrite)}" : string.Empty;
        string deepSeek = _lastDeepSeek is { Hidden: false, Balance: Money balance } ? $" · DeepSeek {Fmt.Amount(balance)}" : string.Empty;
        string openRouter = _lastOpenRouter is { Hidden: false, Message: null } routerUsage
            ? $" · OpenRouter {(routerUsage.LimitUsd is decimal limit && limit > 0 && routerUsage.RemainingUsd is not null ? Fmt.Percent((double)Math.Clamp(routerUsage.UsageUsd / limit * 100, 0, 100)) : Fmt.Amount(new Money(routerUsage.UsageUsd, "USD")))}" : string.Empty;
        string claudePart = claude.Length == 0 ? string.Empty : $" · Claude {claude}";
        _tray?.SetTooltip($"CPU {cpu}{gpu}{claudePart}{codex}{cursor}{openCode}{deepSeek}{openRouter}");
        LogStatusChange();
    }

    private static string CompactTokens(decimal n) => n >= 1_000_000 ? $"{n / 1_000_000m:0.#}M tokens" : n >= 10_000 ? $"{n / 1_000m:0.#}K tokens" : $"{n:N0} tokens";

    /// One log line whenever a source changes between available and failing (not on every refresh).
    private void LogStatusChange()
    {
        string claude = _lastClaude is null ? "pendiente"
            : _lastClaude.Hidden ? "no instalado"
            : _lastClaude.Session is not null ? "ok" : _lastClaude.Message ?? "sin datos";
        string codex = _lastCodex is null ? "pendiente"
            : _lastCodex.Hidden ? "no instalado"
            : _lastCodex.Primary is not null ? "ok" : _lastCodex.Message ?? "sin datos";
        string cursor = _lastCursor is null ? "pendiente"
            : _lastCursor.Hidden ? "no instalado"
            : _lastCursor.Cycle is not null ? "ok" : _lastCursor.Message ?? "sin datos";
        string openCode = _lastOpenCode is null ? "pendiente" : _lastOpenCode.Hidden ? "no instalado" : _lastOpenCode.Message is null ? "ok" : _lastOpenCode.Message;
        string deepSeek = _lastDeepSeek is null ? "pendiente" : _lastDeepSeek.Hidden ? "sin clave" : _lastDeepSeek.Balance is not null ? "ok" : _lastDeepSeek.Message ?? "sin datos";
        string openRouter = _lastOpenRouter is null ? "pendiente" : _lastOpenRouter.Hidden ? "sin clave" : _lastOpenRouter.Message is null ? "ok" : _lastOpenRouter.Message;
        string cpu = _lastCpu is null ? "pendiente" : _lastCpu.Temperature is not null ? "ok" : _lastCpu.Message ?? "sin datos";
        string gpu = _lastGpu is null ? "pendiente"
            : !_lastGpu.Detected ? "no detectada"
            : _lastGpu.Temperature is not null ? "ok" : _lastGpu.Message ?? "sin datos";
        string status = $"Claude: {claude} | Codex: {codex} | Cursor: {cursor} | OpenCode: {openCode} | DeepSeek: {deepSeek} | OpenRouter: {openRouter} | CPU temperatura: {cpu} | GPU temperatura: {gpu}";
        if (status == _lastStatus) return;
        _lastStatus = status;
        Log.Info("Status", status);
    }

    /// The mode lives here: persisted to OpenControlEdge.settings.json, then applied to the panel.
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
        menu.ApiKeysRequested += () => OpenSettings("Agentes");
        menu.ExitRequested += Shutdown;
        menu.AutoStartToggled += SetAutoStart;
        menu.UninstallRequested += ShowUninstallWindow;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };
        _menu = menu;

        // "Iniciar con Windows" and "Desinstalar…" belong to the installed copy only; the switch state comes from
        // schtasks, off the UI thread.
        bool installed = Installer.IsInstalledCopy;
        menu.ShowInstallEntries(installed);
        if (installed)
        {
            Task.Run(AutoStartService.IsEnabled).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) menu.SetAutoStart(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        menu.ShowAtCursor();
    }

    private static void SetAutoStart(bool enabled)
    {
        Task.Run(() =>
        {
            string? error = enabled ? AutoStartService.Enable() : AutoStartService.Disable();
            if (error is null) Log.Info("AutoStart", enabled ? "activado" : "desactivado");
            else Log.Warn("AutoStart", error);
        });
    }

    private void ShowUninstallWindow()
    {
        var window = new InstallWindow(InstallWindow.Mode.Uninstall);
        window.ShowDialog();
        if (window.Result == InstallWindow.Outcome.Uninstalled) Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _refreshTimer?.Stop();
        _sensorTimer?.Stop();
        _warmupTimer?.Stop();
        _autoRenewTimer?.Stop();
        _claudeNoteTimer?.Stop();
        _tray?.Dispose();
        _sensors?.Dispose();
        ThemeManager.Shutdown();
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
