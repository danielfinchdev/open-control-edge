using System.Collections.Frozen;
using System.Collections.Immutable;
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

    // Two sensor cadences: the fast one only while the pinned panel is actually on screen, the slow one the rest of
    // the time. The sensors never stop, so "Máxima de la sesión" also catches the peaks nobody was watching — the
    // boot spike above all.
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
    private SingleInstanceLock? _instanceLock;

    // More accounts (Settings > Agentes): one service each, read together with the default account. The ring shows the
    // account chosen in its card (Settings.SelectedAccounts); everything else (tooltip, agents page) the default one.
    private (ExtraAccount Account, ClaudeUsageService Service)[] _claudeExtras = [];
    private (ExtraAccount Account, CodexUsageService Service)[] _codexExtras = [];
    private (ExtraAccount Account, CursorUsageService Service)[] _cursorExtras = [];
    private ClaudeSnapshot?[] _claudeExtraReadings = [];
    private CodexSnapshot?[] _codexExtraReadings = [];
    private CursorSnapshot?[] _cursorExtraReadings = [];

    /// FPS ring: sampled once a second, only while it is on screen.
    private static readonly TimeSpan FpsInterval = TimeSpan.FromSeconds(1);
    private readonly FpsService _fps = new();
    private DispatcherTimer? _fpsTimer;
    private GameModeSnapshot _gameMode = new(false, false, false, null);

    /// A newer release found by the automatic check: a dot on the settings button and a banner in the settings.
    private UpdateRelease? _availableUpdate;

    /// Automatic checks: at start-up and then at most this often (the hourly timer asks).
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    /// How long an earlier good Claude reading may stand in for HTTP 429 / 5xx errors (FetchClaudePreservingAsync).
    private static readonly TimeSpan ClaudeStaleLimit = TimeSpan.FromMinutes(15);
    private DateTimeOffset? _claudeReadAt;
    private string? _claudeRetryError;
    private DispatcherTimer? _signInWatch;
    private DateTimeOffset? _autoRenewGaveUpOn;
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

    /// The file bridge with Orb (OrbBridge): oce.json written and orb.json read every 2 s off the UI thread, while
    /// «Compartir datos con Orb» is on. _orb is Orb's status while it is connected, null otherwise.
    private readonly OrbBridge _orbBridge = new();
    private DispatcherTimer? _bridgeTimer;
    private Task _bridgeIo = Task.CompletedTask;
    private OrbStatus? _orb;
    private FpsService.Sample? _lastFps;
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
        ThemeManager.Apply(appearance.Theme, appearance.ColorTheme, appearance.PanelBackground);
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

        // --measure pinned|auto (Debug): runs next to the installed copy, in that panel mode, without saving it and off
        // screen, so handoff\mediciones\medir-debug.ps1 can measure the idle cost of a build.
        PanelMode? measureMode = null;
#if DEBUG
        int measureArg = Array.IndexOf(e.Args, "--measure");
        if (measureArg >= 0 && measureArg + 1 < e.Args.Length)
            measureMode = e.Args[measureArg + 1] == "auto" ? PanelMode.Auto : PanelMode.Pinned;
#endif

        if (measureMode is null && !AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        _panelMode = measureMode ?? SettingsStore.Load().PanelMode;
        Log.Info("App", $"started (panel {_panelMode}, {(UnelevatedLauncher.IsElevated ? "elevated" : "not elevated")})");
        // A crash or a power cut in game mode: the PC is still in it. Undo it before anything else.
        if (measureMode is null) _ = GameModeService.RestoreLeftoverAsync();

        // Timers exist before the window is shown: a pinned panel expands during Show() and sets the cadence.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(SettingsStore.Load().UsageRefreshMinutes) };
        _refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        _sensorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SensorHiddenInterval };
        _sensorTimer.Tick += async (_, _) => await RefreshSensorsAsync();

        _sensors = new HardwareSensorService();
        _edge = new EdgeWindow(_sensors.SessionStart, _panelMode);
#if DEBUG
        _edge.Offscreen = measureMode is not null;
#endif
        Settings settings = SettingsStore.Load();
        _edge.ApplyScale(settings.UiScale);
        _edge.ApplyViews(settings, animate: false);
        _gameMode = _gameMode with { Allowed = settings.GameMode.Enabled };
        _edge.SetGameMode(_gameMode);
        SyncAccounts(settings);

        // The last reading, painted before any network request (UsageCache); the first refresh replaces it.
        UsageCache.Cached cached = UsageCache.Load(DateTimeOffset.Now);
        _lastClaude = AiRingPolicy.ShouldShowRing(AiProviderId.Claude, settings)
            ? cached.Claude ?? ClaudeSnapshot.Failed("Cargando…") : ClaudeSnapshot.Absent();
        _claudeReadAt = cached.Claude is not null ? cached.SavedAt : null;
        ShowClaude(settings);
        _edge.ApplyUsageView(settings.UsageView);
        _lastCodex = InitialCodex(settings, cached.Codex);
        ShowCodex(settings);
        _lastCursor = InitialCursor(settings, cached.Cursor);
        ShowCursor(settings);
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
        _edge.CloseRequested += () => _edge.SlideOut(Shutdown);
        _edge.ClaudeClicked += () => _ = RenewClaudeSessionAsync(automatic: false);
        _edge.CpuClicked += OpenSystemInformation;
        _edge.RamClicked += () => _ = FreeRamAsync();
        _edge.ViewChangeRequested += view =>
        {
            Settings? updated = SettingsStore.Update(x => x with { View = view });
            _edge.ApplyViews(updated ?? SettingsStore.Load() with { View = view }, animate: true);
        };
        _edge.AccountSelected += SelectAccount;
        _edge.GameModeClicked += () => _ = ToggleGameModeAsync();
        _edge.VisibleRingsChanged += UpdateFpsSampling;
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
        if (measureMode is null) ConfigureOrbBridge(SettingsStore.Load());
        StartAutomaticUpdateCheck(atStartup: true);
        ConfigureAutoUpdateTimer(SettingsStore.Load());
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.BringToFront(); return; }
        OpenSettings("General", _availableUpdate);
    }

    private void OpenSettings(string category, UpdateRelease? release = null)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            if (release is not null) _settingsWindow.ShowUpdateResult(release);
            else _settingsWindow.BringToFront();
            return;
        }
        Settings previousSettings = SettingsStore.Load();
        _settingsWindow = new SettingsWindow(() => RefreshUsageAsync(), settings =>
        {
            _edge?.ApplyScale(settings.UiScale);
            _edge?.ApplyViews(settings, animate: true);
            ThemeManager.ApplyBackground(settings.PanelBackground);
            ApplyGameModeSetting(settings);
            bool accountsChanged = SyncAccounts(settings);
            if (_panelMode != settings.PanelMode) SetPanelMode(settings.PanelMode);
            SetInterval(_refreshTimer, TimeSpan.FromMinutes(settings.UsageRefreshMinutes));
            ApplyCurrentSettings(settings);
            ConfigureAutoUpdateTimer(settings);
            bool updateCheckEnabled = !previousSettings.AutoCheckUpdates && settings.AutoCheckUpdates;
            if (updateCheckEnabled) StartAutomaticUpdateCheck();
            bool providersChanged = !previousSettings.Providers.OrderBy(x => x.Key).SequenceEqual(settings.Providers.OrderBy(x => x.Key));
            bool refreshChanged = previousSettings.UsageRefreshMinutes != settings.UsageRefreshMinutes;
            if (previousSettings.ShareWithOrb != settings.ShareWithOrb) ConfigureOrbBridge(settings);
            previousSettings = settings;
            if (providersChanged || refreshChanged || accountsChanged) _ = RefreshUsageAsync();
        }, AgentStatus, RetryProviderAsync, category, release ?? _availableUpdate, () => _lastUsageRefresh,
            () => _lastCpu?.Temperature is not null || _lastGpu?.Temperature is not null) { Anchor = _edge };
        _settingsWindow.ContentRendered += (_, _) => Log.Info("Settings", "settings window content rendered");
        if (category == "Agentes" && !_agentsProbed)
        {
            _agentsProbed = true;
            _ = ProbeAgentsAsync(_settingsWindow);
        }
        _settingsWindow.Show();
        _settingsWindow.BringToFront();
    }

    private async Task ProbeAgentsAsync(SettingsWindow window)
    {
        await Task.WhenAll(AiDetector.All.Select(RetryProviderAsync));
        if (window.IsVisible) window.RefreshAgents();
    }

    /// Asks GitHub for the latest release at start-up and then every UpdateCheckInterval. A newer one is not opened in
    /// anybody's face: the settings button gets a dot and the settings window a banner to download and install it.
    private async void StartAutomaticUpdateCheck(bool atStartup = false)
    {
        Settings settings = SettingsStore.Load();
        if (!settings.AutoCheckUpdates) return;
        if (!atStartup && settings.LastAutoUpdateCheck is DateTimeOffset previous && DateTimeOffset.Now - previous < UpdateCheckInterval) return;
        SettingsStore.Update(s => s with { LastAutoUpdateCheck = DateTimeOffset.Now });
        UpdateCheckResult result = await UpdateService.CheckAsync();
        if (result.Release is not null) ShowUpdateAvailable(result.Release);
        else if (result.Error is not null) Log.Warn("Updates", result.Error);
    }

    private void ShowUpdateAvailable(UpdateRelease release)
    {
        if (_availableUpdate?.Version == release.Version) return;
        Log.Info("Updates", $"nueva versión disponible: {release.Tag}");
        _availableUpdate = release;
        _edge?.SetUpdateAvailable(true);
        if (_settingsWindow is { IsVisible: true }) _settingsWindow.ShowUpdateBanner(release);
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
        string authority = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        UpdateCheckResult noDigest = await UpdateService.CheckAsync(authority + "/nodigest").ConfigureAwait(false);
        if (noDigest.Release is not null || noDigest.Error is null) throw new InvalidDataException("a release without digest was accepted");

        // An archive with one file more than the application (a DLL the exe would load elevated) is rejected whole.
        UpdateCheckResult extra = await UpdateService.CheckAsync(authority + "/extra").ConfigureAwait(false);
        if (extra.Release is null) throw new InvalidDataException(extra.Error ?? "fixture /extra returned no release");
        try
        {
            await UpdateService.DownloadAndStageAsync(extra.Release, stage).ConfigureAwait(false);
            throw new InvalidOperationException("an archive with an extra file was accepted");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("no son de Open Control Edge", StringComparison.Ordinal)) { }
        if (Directory.Exists(stage)) throw new InvalidDataException("the rejected archive left a staging folder");

        string payload = await UpdateService.DownloadAndStageAsync(result.Release, stage).ConfigureAwait(false);
        string stagedExe = Path.Combine(payload, "OpenControlEdge.exe");
        if (!File.Exists(stagedExe) || Directory.GetFiles(payload).Length != 1 + NativeLibraries.Sha256.Count)
            throw new InvalidDataException("fixture archive contents were not staged");
        string stagedHash = NativeLibraries.HashHex(stagedExe);
        string target = Path.Combine(AppContext.BaseDirectory, "update-fixture-target");
        string old = Path.Combine(target, "old.marker");
        string backup = target + ".fixture-backup";
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.CreateDirectory(target); await File.WriteAllTextAsync(old, "old");
            // The update helper once ran with its current directory inside the payload, which Windows refuses to rename.
            Directory.SetCurrentDirectory(payload);
            if (!UpdateInstaller.SwapDirectory(payload, target, backup)) throw new InvalidDataException("fixture directory swap failed");
            string swappedExe = Path.Combine(target, "OpenControlEdge.exe");
            if (!File.Exists(swappedExe) || NativeLibraries.HashHex(swappedExe) != stagedHash || File.Exists(old))
                throw new InvalidDataException("fixture directory swap left the wrong contents");
            // Force the failure branch with a missing payload and ensure rollback restores the previous directory.
            string missing = Path.Combine(stage, "missing-payload");
            bool rolledBack = !UpdateInstaller.SwapDirectory(missing, target, backup, failAfterBackup: true)
                && File.Exists(swappedExe);
            if (!rolledBack) throw new InvalidDataException("fixture rollback failed");
            await File.WriteAllTextAsync(resultPath, $"OK {result.Release.Tag}; digest verified; missing and invalid digests rejected; extra file rejected; extraction, swap with the current directory inside the payload and rollback verified").ConfigureAwait(false);
            return true;
        }
        finally
        {
            try { Directory.SetCurrentDirectory(previousDirectory); } catch { }
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

    /// A hidden provider is not read at all (not even its credentials or database), also from the Agents page.
    private async Task RetryProviderAsync(AiProviderId id)
    {
        Settings settings = SettingsStore.Load();
        if (!AiDetector.IsInstalled(id) || !AiRingPolicy.ShouldShowRing(id, settings)) return;
        switch (id)
        {
            case AiProviderId.Claude: _lastClaude = await FetchClaudePreservingAsync(); TrackAgent(id, _lastClaude.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) ShowClaude(settings); break;
            case AiProviderId.Codex: _lastCodex = await _codex.FetchAsync(); TrackAgent(id, _lastCodex.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) ShowCodex(settings); break;
            case AiProviderId.Cursor: _lastCursor = await _cursor.FetchAsync(); TrackAgent(id, _lastCursor.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) ShowCursor(settings); break;
            case AiProviderId.OpenCode: _lastOpenCode = await _openCode.FetchAsync(); TrackAgent(id, _lastOpenCode.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetOpenCode(_lastOpenCode); break;
            case AiProviderId.DeepSeek: _lastDeepSeek = await _deepSeek.FetchAsync(); TrackAgent(id, _lastDeepSeek.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetDeepSeek(_lastDeepSeek); break;
            case AiProviderId.OpenRouter: _lastOpenRouter = await _openRouter.FetchAsync(); TrackAgent(id, _lastOpenRouter.Message); if (AiRingPolicy.ShouldShowRing(id, settings)) _edge?.SetOpenRouter(_lastOpenRouter); break;
        }
        UpdateTooltip();
    }

    private AgentStatus AgentStatus(AiProviderId id)
    {
        if (!AiDetector.IsInstalled(id)) return new("missing", null, null, null);
        if (!AiRingPolicy.ShouldShowRing(id, SettingsStore.Load())) return new("hidden", null, null, null);
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
        return new(AiDetector.NeedsSignIn(message) ? "session" : "error", plan, message ?? "Sin respuesta",
            _agentFailures.TryGetValue(id, out var failure) ? failure.At : null);
    }

    private bool AcquireSingleInstance()
    {
        _instanceLock = SingleInstanceLock.Acquire();
        return _instanceLock is not null;
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
            // A stale Claude reading is not saved again: savedAt would make it look newer than it is.
            UsageCache.Save(_lastClaude is { StaleSince: null } ? _lastClaude : null, _lastCodex, _lastCursor, DateTimeOffset.Now);
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
    private async Task ApplyClaudeAsync(Settings s)
    {
        if (_renewing) return;
        var extras = _claudeExtras;
        Task<ClaudeSnapshot[]> others = AiRingPolicy.ShouldShowRing(AiProviderId.Claude, s)
            ? Task.WhenAll(extras.Select(e => e.Service.FetchAsync())) : Task.FromResult(Array.Empty<ClaudeSnapshot>());
        _lastClaude = await RefreshClaudeAsync(s);
        TrackAgent(AiProviderId.Claude, _lastClaude.Message);
        ClaudeSnapshot[] read = await others;
        if (ReferenceEquals(extras, _claudeExtras)) _claudeExtraReadings = read;
        ShowClaude(s);
    }

    private async Task ApplyCodexAsync(Settings s)
    {
        var extras = _codexExtras;
        Task<CodexSnapshot[]> others = AiRingPolicy.ShouldShowRing(AiProviderId.Codex, s)
            ? Task.WhenAll(extras.Select(e => e.Service.FetchAsync())) : Task.FromResult(Array.Empty<CodexSnapshot>());
        _lastCodex = await RefreshCodexAsync(s);
        TrackAgent(AiProviderId.Codex, _lastCodex.Message);
        CodexSnapshot[] read = await others;
        if (ReferenceEquals(extras, _codexExtras)) _codexExtraReadings = read;
        ShowCodex(s);
    }

    private async Task ApplyCursorAsync(Settings s)
    {
        var extras = _cursorExtras;
        Task<CursorSnapshot[]> others = AiRingPolicy.ShouldShowRing(AiProviderId.Cursor, s)
            ? Task.WhenAll(extras.Select(e => e.Service.FetchAsync())) : Task.FromResult(Array.Empty<CursorSnapshot>());
        _lastCursor = await RefreshCursorAsync(s);
        TrackAgent(AiProviderId.Cursor, _lastCursor.Message);
        CursorSnapshot[] read = await others;
        if (ReferenceEquals(extras, _cursorExtras)) _cursorExtraReadings = read;
        ShowCursor(s);
    }

    /// The ring and card show the chosen account: the default one, or an extra one (still loading until first read).
    private void ShowClaude(Settings s)
    {
        if (_edge is null || _lastClaude is null) return;
        int index = s.SelectedAccount(AiProviderSettings.Claude);
        _edge.SetClaude(_lastClaude.Hidden || index == 0 ? WithOrbUsage(_lastClaude)
            : _claudeExtraReadings.ElementAtOrDefault(index - 1) ?? ClaudeSnapshot.Failed("Cargando…"));
    }

    private void ShowCodex(Settings s)
    {
        if (_edge is null || _lastCodex is null) return;
        int index = s.SelectedAccount(AiProviderSettings.Codex);
        _edge.SetCodex(_lastCodex.Hidden || index == 0 ? WithOrbUsage(_lastCodex)
            : _codexExtraReadings.ElementAtOrDefault(index - 1) ?? CodexSnapshot.Failed("Cargando…"));
    }

    private void ShowCursor(Settings s)
    {
        if (_edge is null || _lastCursor is null) return;
        int index = s.SelectedAccount(AiProviderSettings.Cursor);
        _edge.SetCursor(_lastCursor.Hidden || index == 0 ? WithOrbUsage(_lastCursor)
            : _cursorExtraReadings.ElementAtOrDefault(index - 1) ?? CursorSnapshot.Failed("Cargando…"));
    }

    /// A card's account tab was chosen: saved, then the ring repaints from the readings already in memory.
    private void SelectAccount(AiProviderId provider, int index)
    {
        string key = AiProviderSettings.Key(provider);
        Settings? updated = SettingsStore.Update(x =>
        {
            var map = x.SelectedAccounts.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            map[key] = index;
            return x with { SelectedAccounts = map.ToFrozenDictionary(StringComparer.Ordinal) };
        });
        Settings settings = updated ?? SettingsStore.Load();
        switch (provider)
        {
            case AiProviderId.Claude: ShowClaude(settings); break;
            case AiProviderId.Codex: ShowCodex(settings); break;
            case AiProviderId.Cursor: ShowCursor(settings); break;
        }
    }

    /// Rebuilds the services of the extra accounts when they changed and refreshes the account tabs. Returns whether
    /// the accounts changed (then they have to be read).
    private bool SyncAccounts(Settings settings)
    {
        bool changed = false;
        ImmutableArray<ExtraAccount> claude = settings.AccountsOf(AiProviderSettings.Claude);
        if (!claude.SequenceEqual(_claudeExtras.Select(e => e.Account)))
        {
            _claudeExtras = [.. claude.Select(a => (a, new ClaudeUsageService(Path.Combine(a.Folder, ".credentials.json"))))];
            _claudeExtraReadings = [];
            changed = true;
        }
        ImmutableArray<ExtraAccount> codex = settings.AccountsOf(AiProviderSettings.Codex);
        if (!codex.SequenceEqual(_codexExtras.Select(e => e.Account)))
        {
            _codexExtras = [.. codex.Select(a => (a, new CodexUsageService(Path.Combine(a.Folder, "auth.json"))))];
            _codexExtraReadings = [];
            changed = true;
        }
        ImmutableArray<ExtraAccount> cursor = settings.AccountsOf(AiProviderSettings.Cursor);
        if (!cursor.SequenceEqual(_cursorExtras.Select(e => e.Account)))
        {
            _cursorExtras = [.. cursor.Select(a => (a, new CursorUsageService(Path.Combine(a.Folder, "User", "globalStorage", "state.vscdb"))))];
            _cursorExtraReadings = [];
            changed = true;
        }
        if (_edge is not null)
        {
            string main = Loc.Get("Account.Default");
            _edge.SetAccounts(AiProviderId.Claude, [main, .. claude.Select(a => a.Name)], settings.SelectedAccount(AiProviderSettings.Claude));
            _edge.SetAccounts(AiProviderId.Codex, [main, .. codex.Select(a => a.Name)], settings.SelectedAccount(AiProviderSettings.Codex));
            _edge.SetAccounts(AiProviderId.Cursor, [main, .. cursor.Select(a => a.Name)], settings.SelectedAccount(AiProviderSettings.Cursor));
            if (changed)
            {
                ShowClaude(settings);
                ShowCodex(settings);
                ShowCursor(settings);
            }
        }
        return changed;
    }

    // ───────────────────────────── FPS and Modo juego ─────────────────────────────

    /// The ETW session and the one-second timer exist only while the FPS ring is on screen.
    private void UpdateFpsSampling()
    {
        if (_edge is null) return;
        bool onScreen = _edge.IsRingOnScreen(EdgeWindow.RingFps);
        if (onScreen && _fpsTimer is not { IsEnabled: true })
        {
            _fps.Start();
            _fpsTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = FpsInterval };
            _fpsTimer.Tick -= OnFpsTick;
            _fpsTimer.Tick += OnFpsTick;
            _fpsTimer.Start();
        }
        else if (!onScreen && _fpsTimer is { IsEnabled: true })
        {
            _fpsTimer.Stop();
            _fps.Stop();
            _lastFps = null;
        }
    }

    private void OnFpsTick(object? sender, EventArgs e)
    {
        _lastFps = _fps.Read();
        _edge?.SetFps(_lastFps);
    }

    /// The ring switches game mode on and off; while it is not enabled in the settings it opens that page instead.
    private async Task ToggleGameModeAsync()
    {
        if (_edge is null || _gameMode.Busy) return;
        Settings settings = SettingsStore.Load();
        if (!settings.GameMode.Enabled)
        {
            OpenSettings("Modo juego");
            return;
        }
        bool activate = !GameModeService.IsActive;
        _gameMode = _gameMode with { Allowed = true, Busy = true };
        _edge.SetGameMode(_gameMode);
        GameModeResult result = activate
            ? await GameModeService.ActivateAsync(settings.GameMode)
            : await GameModeService.DeactivateAsync();
        _gameMode = new GameModeSnapshot(true, result.Active, false, result);
        _edge.SetGameMode(_gameMode);
        // The power plan and the closed programs change the readings: read the sensors now.
        _ = RefreshSensorsAsync();
    }

    /// Settings changed: switching the option off while game mode is on undoes it.
    private void ApplyGameModeSetting(Settings settings)
    {
        if (_edge is null) return;
        if (!settings.GameMode.Enabled && GameModeService.IsActive && !_gameMode.Busy)
        {
            _ = Task.Run(async () =>
            {
                GameModeResult result = await GameModeService.DeactivateAsync();
                await Dispatcher.InvokeAsync(() =>
                {
                    _gameMode = new GameModeSnapshot(false, false, false, result);
                    _edge?.SetGameMode(_gameMode);
                });
            });
            return;
        }
        if (_gameMode.Allowed == settings.GameMode.Enabled) return;
        _gameMode = _gameMode with { Allowed = settings.GameMode.Enabled };
        _edge.SetGameMode(_gameMode);
    }

    // ───────────────────────────── Orb ─────────────────────────────

    /// «Compartir datos con Orb» on: the exchange runs every 2 s. Off: it stops, Orb's data leaves the panel and
    /// oce.json is deleted once any exchange in flight has finished (so that one cannot write it again).
    private void ConfigureOrbBridge(Settings settings)
    {
        if (settings.ShareWithOrb)
        {
            if (_bridgeTimer is null)
            {
                _bridgeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = OrbBridge.WriteInterval };
                _bridgeTimer.Tick += (_, _) => ExchangeWithOrb();
            }
            if (_bridgeTimer.IsEnabled) return;
            _bridgeTimer.Start();
            ExchangeWithOrb();
            Log.Info("Orb", "compartir datos con Orb: activado");
            return;
        }
        _bridgeTimer?.Stop();
        ApplyOrb(null);
        _bridgeIo = _bridgeIo.ContinueWith(_ => _orbBridge.Delete(), TaskScheduler.Default);
        Log.Info("Orb", "compartir datos con Orb: desactivado");
    }

    /// One exchange: OCE's readings are gathered here, the files are written and read on a worker thread, and Orb's
    /// status comes back here. Skipped while the previous one is still running.
    private async void ExchangeWithOrb()
    {
        if (!_bridgeIo.IsCompleted) return;
        OceReading reading = OwnReading();
        Task<OrbStatus?> io = Task.Run(() => _orbBridge.Exchange(reading with
        {
            RamUsedPct = MemoryService.Read() is { Message: null } ram ? ram.Percent : null,
        }));
        _bridgeIo = io;
        try
        {
            OrbStatus? orb = await io;
            if (_bridgeTimer is { IsEnabled: true }) ApplyOrb(orb);
        }
        catch (Exception ex)
        {
            Log.Error("Orb", ex);
        }
    }

    /// OCE's own last readings, for oce.json: the default account of each provider and the short window (Claude's
    /// 5 h session, Codex's shorter window, Cursor's cycle), never anything that came from Orb. Providers without a
    /// percentage (OpenCode, DeepSeek; OpenRouter without a limit) say only that they are read, with null values.
    private OceReading OwnReading()
    {
        var usage = ImmutableArray.CreateBuilder<OceUsage>();
        if (_lastClaude is { Hidden: false, Session: UsageWindow session } claude)
            usage.Add(new OceUsage("claude", session.Percent, session.ResetsAt, claude.Plan));
        if (_lastCodex is { Hidden: false, Primary: CodexWindow primary } codex)
        {
            CodexWindow shorter = codex.Secondary is CodexWindow secondary
                && (primary.Length ?? TimeSpan.Zero) > (secondary.Length ?? TimeSpan.MaxValue) ? secondary : primary;
            usage.Add(new OceUsage("codex", shorter.Percent, shorter.ResetsAt, codex.Plan));
        }
        if (_lastCursor is { Hidden: false, Cycle: UsageWindow cycle } cursor)
            usage.Add(new OceUsage("cursor", cycle.Percent, cycle.ResetsAt, cursor.Plan));
        if (_lastOpenCode is { Hidden: false, Message: null })
            usage.Add(new OceUsage("opencode", null, null, null));
        if (_lastDeepSeek is { Hidden: false, Balance: not null })
            usage.Add(new OceUsage("deepseek", null, null, null));
        if (_lastOpenRouter is { Hidden: false, Message: null } router)
            usage.Add(new OceUsage("openrouter", router.LimitUsd is decimal limit && limit > 0
                ? (double)Math.Clamp(router.UsageUsd / limit * 100, 0, 100) : null, null, null));
        return new OceReading(_lastCpu, _lastGpu, null, _lastFps?.Fps, usage.ToImmutable());
    }

    /// Orb's status after an exchange (null: disconnected). The badge, and the Claude, Codex and Cursor rings, are
    /// repainted only when what they show of Orb changed: every repaint redraws the whole layered window.
    private void ApplyOrb(OrbStatus? orb)
    {
        OrbStatus? previous = _orb;
        _orb = orb;
        bool connectionChanged = (previous is null) != (orb is null);
        if (connectionChanged)
            Log.Info("Orb", orb is null ? "Orb desconectado" : $"Orb conectado (versión {orb.Version ?? "sin datos"})");
        if (_edge is null) return;
        if (connectionChanged || previous?.Tasks != orb?.Tasks || previous?.AssistantBusy != orb?.AssistantBusy)
            _edge.SetOrb(orb);
        Settings settings = SettingsStore.Load();
        if (!_renewing && previous?.UsageFor("claude") != orb?.UsageFor("claude")) ShowClaude(settings);
        if (previous?.UsageFor("codex") != orb?.UsageFor("codex")) ShowCodex(settings);
        if (previous?.UsageFor("cursor") != orb?.UsageFor("cursor")) ShowCursor(settings);
    }

    /// OCE's own reading whenever it has one; otherwise Orb's for that agent, marked with its source, while Orb is
    /// connected and has a percentage. Never for a hidden ring; the plan badge stays empty (Orb's account may be
    /// another one). Only shown: never cached, never written to oce.json.
    private ClaudeSnapshot WithOrbUsage(ClaudeSnapshot own) =>
        own is { Hidden: false, Session: null } && _orb?.UsageFor("claude") is { UsedPct: double used } orb
            ? new ClaudeSnapshot(false, new UsageWindow(used, orb.ResetAt), null, null, null) { Source = OrbSource(orb) }
            : own;

    private CodexSnapshot WithOrbUsage(CodexSnapshot own) =>
        own is { Hidden: false, Primary: null } && _orb?.UsageFor("codex") is { UsedPct: double used } orb
            ? new CodexSnapshot(false, new CodexWindow(used, null, orb.ResetAt), null, null) { Source = OrbSource(orb) }
            : own;

    private CursorSnapshot WithOrbUsage(CursorSnapshot own) =>
        own is { Hidden: false, Cycle: null } && _orb?.UsageFor("cursor") is { UsedPct: double used } orb
            ? new CursorSnapshot(false, new UsageWindow(used, orb.ResetAt), null, null) { Source = OrbSource(orb) }
            : own;

    /// "Personal · 5h": the account and window Orb names, "" when it names neither.
    private static string OrbSource(OrbUsage usage) =>
        string.Join(" · ", new[] { usage.Label, usage.Window }.Where(part => part is not null));

    /// Closing: no more exchanges, and oce.json goes, so Orb shows Open Control Edge as disconnected at once.
    private void StopOrbBridge()
    {
        bool sharing = _bridgeTimer is { IsEnabled: true };
        _bridgeTimer?.Stop();
        try { _bridgeIo.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        if (sharing) _orbBridge.Delete();
    }

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
            : DeepSeekSnapshot.Failed(AiDetector.AddKeyMessage);
    }

    private OpenRouterSnapshot InitialOpenRouter(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.OpenRouter, settings)) return OpenRouterSnapshot.Absent();
        return AiDetector.IsInstalled(AiProviderId.OpenRouter) ? OpenRouterSnapshot.Failed("Cargando…")
            : OpenRouterSnapshot.Failed(AiDetector.AddKeyMessage);
    }

    private async Task<ClaudeSnapshot> RefreshClaudeAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.Claude, settings)) return ClaudeSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.Claude, settings))
            return ClaudeSnapshot.Failed(AiDetector.ClaudeLoginMessage);
        return await FetchClaudePreservingAsync();
    }

    /// After an HTTP 429 or 5xx the last good reading stays on screen, marked with its age, for up to
    /// ClaudeStaleLimit; after that the error shows. While the service asks to wait (RetryAfterUntil) nothing is sent.
    private async Task<ClaudeSnapshot> FetchClaudePreservingAsync()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (_claude.RetryAfterUntil is DateTimeOffset retryAt && DateTimeOffset.UtcNow < retryAt && _claudeRetryError is not null)
            return KeepClaudeReading(now) ?? ClaudeSnapshot.Failed(_claudeRetryError) with
            {
                Plan = _lastClaude?.Plan, TokenExpiresAt = _lastClaude?.TokenExpiresAt,
            };

        ClaudeSnapshot current = await _claude.FetchAsync();
        _claudeRetryError = null;
        if (current is { Hidden: false, Session: not null })
        {
            _claudeReadAt = DateTimeOffset.Now;
            return current;
        }
        if (current is { HttpStatus: int status, Message: string error } && (status == 429 || status >= 500))
        {
            _claudeRetryError = error;
            if (KeepClaudeReading(DateTimeOffset.Now) is ClaudeSnapshot kept)
                return kept with { Plan = current.Plan ?? kept.Plan, TokenExpiresAt = current.TokenExpiresAt };
        }
        return current;
    }

    /// The last good Claude reading marked as stale, or null when there is none or it is older than ClaudeStaleLimit.
    private ClaudeSnapshot? KeepClaudeReading(DateTimeOffset now) =>
        _lastClaude is { Hidden: false, Session: not null } previous && _claudeReadAt is DateTimeOffset readAt
        && now - readAt < ClaudeStaleLimit
            ? previous with { StaleSince = readAt }
            : null;

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
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.DeepSeek, settings)) return DeepSeekSnapshot.Failed(AiDetector.AddKeyMessage);
        return await _deepSeek.FetchAsync();
    }

    private async Task<OpenRouterSnapshot> RefreshOpenRouterAsync(Settings settings)
    {
        if (!AiRingPolicy.ShouldShowRing(AiProviderId.OpenRouter, settings)) return OpenRouterSnapshot.Absent();
        if (!AiRingPolicy.ShouldFetchUsage(AiProviderId.OpenRouter, settings)) return OpenRouterSnapshot.Failed(AiDetector.AddKeyMessage);
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

            // Not renewed: the background renewal gives up on this token until expiresAt changes (a click still tries).
            _autoRenewGaveUpOn = result.Outcome is RenewOutcome.NotRenewed or RenewOutcome.SignedOut
                ? result.ExpiresAt ?? ClaudeSessionRenewer.ReadExpiry() ?? DateTimeOffset.MinValue
                : null;

            // Signed out: nothing to renew. A click opens the sign-in (the one way to sign in again).
            string? loginError = null;
            if (result.Outcome == RenewOutcome.SignedOut && !automatic)
            {
                loginError = ClaudeSessionRenewer.StartLogin();
                if (loginError is null) WatchClaudeSignIn();
            }

            _renewing = false;
            await ApplyClaudeAsync(SettingsStore.Load());
            UpdateTooltip();
            ArmAutoRenew();
            if (!automatic) ShowClaudeNote(result, loginError);
        }
        catch (Exception ex)
        {
            Log.Error("Claude renew", ex);
            _renewing = false;
            ShowClaude(SettingsStore.Load());
            _edge.SetClaudeNote(Loc.Message("No se pudo renovar la sesión"));
        }
        finally
        {
            _renewing = false;
        }
    }

    /// The outcome of a clicked renewal under the Claude card, for ClaudeNoteDuration.
    private void ShowClaudeNote(RenewResult result, string? loginError = null)
    {
        if (_edge is null) return;
        DateTime? until = result.ExpiresAt?.LocalDateTime;
        string note = result.Outcome switch
        {
            RenewOutcome.Renewed when until is DateTime at => Loc.Format("Claude.Renewed", at),
            RenewOutcome.StillValid when until is DateTime at => Loc.Format("Claude.StillValid", at),
            RenewOutcome.SignedOut => Loc.Message(loginError ?? ClaudeSessionRenewer.LoginOpenedMessage)!,
            RenewOutcome.CliMissing => Loc.Message(ClaudeSessionRenewer.CliMissingMessage)!,
            RenewOutcome.TimedOut => Loc.Message(ClaudeSessionRenewer.TimedOutMessage)!,
            RenewOutcome.Failed when result.Error == UnelevatedLauncher.SeclogonMessage => Loc.Message(UnelevatedLauncher.SeclogonMessage)!,
            RenewOutcome.Failed => Loc.Message(ClaudeSessionRenewer.StartFailedMessage)!,
            _ => Loc.Message(ClaudeSessionRenewer.NotRenewedMessage)!,
        };
        _edge.SetClaudeNote(note);

        _claudeNoteTimer ??= CreateOneShot(() => _edge?.SetClaudeNote(null));
        _claudeNoteTimer.Stop();
        _claudeNoteTimer.Interval = ClaudeNoteDuration;
        _claudeNoteTimer.Start();
    }

    /// After the sign-in window opened: checks the credentials every 5 s, for up to 5 minutes, and reads Claude usage
    /// again as soon as there is a sign-in, so the ring does not wait for the next refresh.
    internal void WatchClaudeSignIn()
    {
        _signInWatch?.Stop();
        DateTime until = DateTime.UtcNow.AddMinutes(5);
        _signInWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _signInWatch.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow > until) { _signInWatch?.Stop(); return; }
            if (!await Task.Run(() => ClaudeSessionRenewer.HasSignIn)) return;
            _signInWatch?.Stop();
            Log.Info("Claude", "sesión iniciada en Claude Code");
            _edge?.SetClaudeNote(null);
            await ApplyClaudeAsync(SettingsStore.Load());
            UpdateTooltip();
            ArmAutoRenew();
        };
        _signInWatch.Start();
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
        if (expiry == _autoRenewGaveUpOn) return;
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
            _edge.SetRamNote(Loc.Format("Ram.Freed", (result.FreedBytes / (1024 * 1024)).ToString("N0", Loc.Culture)));
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

    /// Sensors: every 20 s while the pinned panel is on screen, every minute otherwise (10 s during the warm-up); they
    /// never stop, so the session maximum stays honest. Usage: the interval chosen in the settings, always.
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
        menu.ExitRequested += () => { if (_edge is null) Shutdown(); else _edge.SlideOut(Shutdown); };
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
        GameModeService.DeactivateOnExit();
        StopOrbBridge();
        _fpsTimer?.Stop();
        _fps.Dispose();
        _refreshTimer?.Stop();
        _sensorTimer?.Stop();
        _warmupTimer?.Stop();
        _autoRenewTimer?.Stop();
        _claudeNoteTimer?.Stop();
        _signInWatch?.Stop();
        _tray?.Dispose();
        _sensors?.Dispose();
        ThemeManager.Shutdown();
        _instanceLock?.Dispose();
        Log.Info("App", "exit");
        base.OnExit(e);
    }
}
