using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;
using static OpenControlEdge.Interop.NativeMethods;

namespace OpenControlEdge.Views;

/// One transparent, topmost tool window glued to the right edge of the primary screen. It hosts the
/// collapsed strip, the ring panel and the detail card, so the card can glide between rings with render
/// animations instead of moving HWNDs.
///
/// Two modes: Pinned (panel always expanded, default) and Auto (collapses to a strip, expands on hover).
/// The panel ends in four round buttons, two by two: pin/hide and view above, settings and close below. It shows the
/// rings of one view (IA, PC or the user's own mix, in the order set in Settings); a ring is on screen when its view
/// includes it and it is available (provider installed, GPU detected). Switching views fades the rings out and in.
/// Everything is laid out in design units and scaled as a whole to the monitor's work area (ApplyScale).
/// Hover is driven by polling the cursor position: while collapsed the window is click-through
/// (WS_EX_TRANSPARENT) and receives no mouse input at all, so events could not detect the strip. The poll stops
/// while nothing can change: pinned at rest (the window's own MouseMove restarts it) and collapsed with the pointer
/// still (raw input restarts it).
public partial class EdgeWindow : Window
{
    internal const int RingClaude = 0;
    internal const int RingCodex = 1;
    internal const int RingCursor = 2;
    internal const int RingOpenCode = 3;
    internal const int RingDeepSeek = 4;
    internal const int RingOpenRouter = 5;
    internal const int RingCpu = 6;
    internal const int RingGpu = 7;
    internal const int RingRam = 8;
    internal const int RingFps = 9;
    internal const int RingGameMode = 10;

    /// The settings key of each ring, by index.
    private static readonly string[] RingKeyOf =
    [
        RingKeys.Claude, RingKeys.Codex, RingKeys.Cursor, RingKeys.OpenCode, RingKeys.DeepSeek, RingKeys.OpenRouter,
        RingKeys.Cpu, RingKeys.Gpu, RingKeys.Ram, RingKeys.Fps, RingKeys.GameMode,
    ];

    // Geometry in design units — the original design scaled to 85 %. RootScale maps them to DIPs.
    private const double PanelWidth = 94;
    private const double StripWidth = 5;
    private const double StripHeight = 400;
    private const double CardBodyWidth = 255;
    private const double BeakLength = 16;
    private const double CardGap = 6;
    private const double CardSideRoom = 12;
    private const double CardVerticalRoom = 16;  // keep the card inside the window
    private const double WindowWidthDip = CardSideRoom + CardBodyWidth + BeakLength + CardGap + PanelWidth;

    // Automatic scale: 1.0 on a 1080p work area at 100 %, bigger on taller screens, never cut off.
    private const double ReferenceWorkHeight = 1040;
    private const double MinAutoScale = 0.8;
    private const double MaxAutoScale = 1.4;
    private const double MinFitScale = 0.6;       // only reached when the largest view would not fit otherwise
    private const double ScreenMargin = 4;        // DIPs kept free above and below the panel

    // Round buttons, two by two.
    private const double ButtonDiameter = 32;
    private const double ButtonGap = 8;

    // View switch: the rings fade out (and rise a little), the new ones fade in from below.
    private const int ViewFadeOutMs = 140;
    private const int ViewFadeInMs = 220;
    private const double ViewShift = 8;

    private double _scale = 1;
    private double _windowHeightDip = ReferenceWorkHeight;
    private double _availableHeight = ReferenceWorkHeight;  // design units
    private double? _uiScale;

    private const int PanelMs = 250;
    private const int RingHoverMs = 150;
    private const int CardSlideMs = 250;
    private const double RingHoverScale = 1.08;
    private static readonly TimeSpan CollapseGrace = TimeSpan.FromMilliseconds(300);

    /// A reading that moves a ring or bar by less than this (3 % of the arc) jumps there in one frame: every animated
    /// frame repaints the whole layered window, and a sensor sample that moved a degree is not worth 36 of them.
    private const double MinAnimatedChange = 0.03;

    // Pointer polling: NearPoll only while the cursor is over this window, FarPoll the rest of the time
    // (the overwhelmingly common case), where a tick is a GetCursorPos plus four integer comparisons.
    private static readonly TimeSpan FarPoll = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan NearPoll = TimeSpan.FromMilliseconds(30);
    // Collapsed with the pointer still this long: the poll sleeps until raw input says the pointer moves again.
    private static readonly TimeSpan PointerRestBeforeSleep = TimeSpan.FromSeconds(2);

    private static readonly IEasingFunction EaseOut = CreateEase(EasingMode.EaseOut);
    private static readonly IEasingFunction EaseIn = CreateEase(EasingMode.EaseIn);

    private readonly DateTime _sessionStart;
    private readonly DispatcherTimer _pointerWatch;
    private readonly DispatcherTimer _timeTextTimer;
    private readonly Stopwatch _outside = new();
    private readonly Stopwatch _tickClock = new();
    private readonly Stopwatch _pointerStill = new();
    private POINT _lastCursor;
    private bool _wakeOnInput;
    private readonly FrameworkElement[] _ringItems;
    private readonly RingGauge[] _rings;
    private readonly FrameworkElement[] _cards;
    private PanelMode _mode;
    private bool _expanded;
    private bool _cardVisible;
    private int _activeRing = -1;
    private int _hoveredRing = -1;
    private double _panelTop;
    private bool _hasWindowRect;
    private ClaudeSnapshot? _claude;
    private CodexSnapshot? _codex;
    private CodexWindow? _codexShown;
    private CursorSnapshot? _cursor;
    private OpenCodeSnapshot? _openCode;
    private DeepSeekSnapshot? _deepSeek;
    private OpenRouterSnapshot? _openRouter;
    private CpuSnapshot? _cpu;
    private GpuSnapshot? _gpu;
    private RamSnapshot? _ram;
    private string? _ramNote;
    private UsageView _usageView = UsageView.Session;
    private bool _syncingTabs;
    private readonly bool[] _available;
    private readonly bool[] _inView;
    private Settings _views = Settings.Defaults;
    private WidgetView _view = WidgetView.Ai;
    private ViewLayout _layout = ViewLayout.Default(WidgetView.Ai);
    private int _maxRings;
    private DispatcherTimer? _viewSwitch;
    private Action? _pendingLayout;
    private FpsService.Sample? _fps;
    private GameModeSnapshot? _gameMode;
    private bool _syncingAccounts;

    internal bool AnimationsEnabled { get; set; } = true;

    /// Snapshot rendering: no positioning, no topmost juggling, no pointer polling.
    internal bool PreviewMode { get; set; }

#if DEBUG
    /// --measure: laid out, rendered and polled as usual, but placed beyond every monitor, so a copy measured next to
    /// the installed one is not in anybody's way.
    internal bool Offscreen { get; set; }
#endif

    internal event Action<bool>? ExpandedChanged;

    /// The pin/hide button was pressed: pinned → auto or auto → pinned. The owner persists it and calls ApplyMode.
    internal event Action<PanelMode>? ModeChangeRequested;

    /// The "Sesión" / "Total" tab of a card was switched; rings and card already show it. The owner persists it.
    internal event Action<UsageView>? UsageViewChanged;

    /// The view button was pressed: the owner persists the next view and calls ApplyViews.
    internal event Action<WidgetView>? ViewChangeRequested;

    /// An account tab of a card was chosen (0 = the default account). The owner persists it and repaints the ring.
    internal event Action<AiProviderId, int>? AccountSelected;

    /// The Modo juego ring was clicked: switch it (or open its settings when it is not enabled).
    internal event Action? GameModeClicked;

    /// Which rings are on screen changed (view, panel open or closed): the owner starts or stops the FPS sampling.
    internal event Action? VisibleRingsChanged;

    /// The gear button was pressed: the owner opens the settings.
    internal event Action? SettingsRequested;

    /// The panel's close button was pressed: quit the application.
    internal event Action? CloseRequested;

    /// The Claude ring was clicked: renew the session so the ring stops reading "--".
    internal event Action? ClaudeClicked;

    /// The CPU ring was clicked: open Settings > System > About.
    internal event Action? CpuClicked;

    /// The RAM ring was clicked: free memory.
    internal event Action? RamClicked;

    internal EdgeWindow(DateTime sessionStart, PanelMode mode)
    {
        _sessionStart = sessionStart;
        _mode = mode;
        InitializeComponent();

        _ringItems = new FrameworkElement[] { ClaudeItem, CodexItem, CursorItem, OpenCodeItem, DeepSeekItem, OpenRouterItem, CpuItem, GpuItem, RamItem, FpsItem, GameModeItem };
        _rings = new[] { ClaudeRing, CodexRing, CursorRing, OpenCodeRing, DeepSeekRing, OpenRouterRing, CpuRing, GpuRing, RamRing, FpsRing, GameModeRing };
        _cards = new FrameworkElement[] { ClaudeCard, CodexCard, CursorCard, OpenCodeCard, DeepSeekCard, OpenRouterCard, CpuCard, GpuCard, RamCard, FpsCard, GameModeCard };
        // What the XAML shows at first is what is available before any reading (Claude, CPU, RAM, FPS, Modo juego).
        _available = _ringItems.Select(item => item.Visibility == Visibility.Visible).ToArray();
        _inView = new bool[_ringItems.Length];
        ApplyRingBrushes();
        SetRamNote(null);
        SetGameMode(new GameModeSnapshot(false, false, false, null));
        ApplyLayout(_layout);

        Left = -32000;
        Top = -32000;

        SyncModeButton();
        SyncViewButton();
        SyncUsageTabs();
        Canvas.SetLeft(EdgePanel, WindowWidthDip - PanelWidth);
        Canvas.SetLeft(Card, CardSideRoom);
        Card.Width = CardBodyWidth + BeakLength;
        Card.Height = 0;
        LayOut(ReferenceWorkHeight);

        _pointerWatch = new DispatcherTimer(DispatcherPriority.Input) { Interval = FarPoll };
        _pointerWatch.Tick += OnPointerTick;
        _timeTextTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _timeTextTimer.Tick += (_, _) => RefreshTimeTexts();
        MouseMove += OnWindowMouseMove;

        ThemeManager.Changed += Reapply;
        Loc.Changed += Reapply;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        RefreshTimeTexts();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.Changed -= Reapply;
        Loc.Changed -= Reapply;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _pointerWatch.Stop();
        _timeTextTimer.Stop();
        _viewSwitch?.Stop();
        if (_wakeOnInput) WatchRawPointerInput(IntPtr.Zero, false);
    }

    // ───────────────────────────── Mode & panel buttons ─────────────────────────────

    internal void ApplyMode(PanelMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Log.Trace("Edge", $"mode -> {mode}");

        if (mode == PanelMode.Auto)
        {
            // The chevron keeps pointing right while the panel slides out; it is refreshed on the next expand.
            Collapse();
        }
        else if (_expanded)
        {
            // Pinned from a hover-expanded panel: it simply stays open.
            SyncModeButton();
        }
        else
        {
            Expand();
        }
    }

    /// Pinned: chevron to the right ("hide into the edge"). Auto: chevron to the left ("bring the panel out").
    private void SyncModeButton()
    {
        bool pinned = _mode == PanelMode.Pinned;
        ModeIcon.Icon = pinned ? Icons.ChevronRight : Icons.ChevronLeft;
        string tip = pinned ? "Tip.Hide" : "Tip.Pin";
        ModeButton.SetResourceReference(ToolTipProperty, tip);
        ModeButton.SetResourceReference(AutomationProperties.NameProperty, tip);
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        PanelMode target = _mode == PanelMode.Pinned ? PanelMode.Auto : PanelMode.Pinned;
        Log.Trace("Edge", $"mode button clicked -> {target}");
        ModeChangeRequested?.Invoke(target);
    }

    private void OnViewClick(object sender, RoutedEventArgs e)
    {
        WidgetView next = RingKeys.Next(_view);
        Log.Trace("Edge", $"view button clicked -> {next}");
        ViewChangeRequested?.Invoke(next);
    }

    /// The tooltip names the view the button leads to.
    private void SyncViewButton()
    {
        string tip = Loc.Format("Tip.View", Loc.Get("View." + RingKeys.Next(_view)));
        ViewButton.ToolTip = tip;
        AutomationProperties.SetName(ViewButton, tip);
    }

    internal WidgetView View => _view;

    /// A new version is available: a red dot on the settings button and a tooltip that says so.
    internal void SetUpdateAvailable(bool available)
    {
        UpdateDot.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        string tip = available ? "Tip.SettingsUpdate" : "Tip.Settings";
        SettingsButton.SetResourceReference(ToolTipProperty, tip);
        SettingsButton.SetResourceReference(AutomationProperties.NameProperty, tip);
    }

    /// The view and the layouts of the settings. A different view (or a different order or set of rings in it) fades
    /// the rings out and the new ones in when animate and the panel is on screen; otherwise it changes in one step.
    internal void ApplyViews(Settings settings, bool animate)
    {
        _views = settings;
        ViewLayout layout = settings.Layout(settings.View);
        bool changed = settings.View != _view || !layout.Equals(_layout);
        _view = settings.View;
        _layout = layout;
        SyncViewButton();
        if (!changed)
        {
            UpdateMaxRings();
            return;
        }
        Log.Trace("Edge", $"view -> {_view} ({string.Join(",", layout.Shown)})");
        if (animate && _expanded && AnimationsEnabled && !PreviewMode) SwitchAnimated(() => ApplyLayout(layout));
        else
        {
            FinishViewSwitch();
            ApplyLayout(layout);
        }
    }

    /// Fade out, swap the rings, fade in. A switch already running jumps to its end first.
    private void SwitchAnimated(Action apply)
    {
        FinishViewSwitch();
        if (_hoveredRing >= 0) { ScaleRing(_hoveredRing, 1.0); _hoveredRing = -1; }
        HideCard();
        _pendingLayout = apply;
        Animate(RingStack, OpacityProperty, 0, ViewFadeOutMs, ease: EaseIn);
        Animate(RingShift, TranslateTransform.YProperty, -ViewShift, ViewFadeOutMs, ease: EaseIn);
        _viewSwitch ??= new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ViewFadeOutMs) };
        _viewSwitch.Tick -= OnViewSwitchTick;
        _viewSwitch.Tick += OnViewSwitchTick;
        _viewSwitch.Start();
    }

    private void OnViewSwitchTick(object? sender, EventArgs e)
    {
        _viewSwitch?.Stop();
        Action? apply = _pendingLayout;
        _pendingLayout = null;
        apply?.Invoke();
        Animate(RingShift, TranslateTransform.YProperty, 0, ViewFadeInMs, from: ViewShift);
        Animate(RingStack, OpacityProperty, 1, ViewFadeInMs);
    }

    private void FinishViewSwitch()
    {
        if (_viewSwitch is { IsEnabled: true }) OnViewSwitchTick(null, EventArgs.Empty);
        RingStack.BeginAnimation(OpacityProperty, null);
        RingShift.BeginAnimation(TranslateTransform.YProperty, null);
        RingStack.Opacity = 1;
        RingShift.Y = 0;
    }

    /// The rings in the order of the layout (the ones of other views after them, hidden), then visibility.
    private void ApplyLayout(ViewLayout layout)
    {
        var order = new List<FrameworkElement>();
        foreach (string key in layout.Order)
        {
            int index = Array.IndexOf(RingKeyOf, key);
            if (index >= 0) order.Add(_ringItems[index]);
        }
        foreach (FrameworkElement item in _ringItems)
            if (!order.Contains(item)) order.Add(item);
        if (!order.SequenceEqual(RingStack.Children.Cast<FrameworkElement>()))
        {
            RingStack.Children.Clear();
            foreach (FrameworkElement item in order) RingStack.Children.Add(item);
        }
        var shown = layout.Shown.ToHashSet();
        for (int i = 0; i < _inView.Length; i++) _inView[i] = shown.Contains(RingKeyOf[i]);
        ApplyRingVisibility();
    }

    /// A ring is on screen when its view shows it and it is available. The first one carries no top margin, so the
    /// panel starts at the same place whichever it is.
    private void ApplyRingVisibility()
    {
        bool first = true;
        foreach (FrameworkElement item in RingStack.Children)
        {
            int index = Array.IndexOf(_ringItems, item);
            bool visible = _available[index] && _inView[index];
            var target = visible ? Visibility.Visible : Visibility.Collapsed;
            if (item.Visibility != target)
            {
                item.Visibility = target;
                if (!visible)
                {
                    if (_hoveredRing == index)
                    {
                        ScaleRing(index, 1.0);
                        _hoveredRing = -1;
                    }
                    if (_cardVisible && _activeRing == index) HideCard();
                }
            }
            if (visible)
            {
                item.Margin = new Thickness(0, first ? 0 : 10, 0, 0);
                first = false;
            }
        }

        UpdateMaxRings();
        CenterPanel(animate: _expanded);
        if (_cardVisible) PlaceCard(animate: true);
        VisibleRingsChanged?.Invoke();
    }

    /// The most rings any view shows with what is available now: the scale is chosen for that many, so switching
    /// views never resizes the panel.
    private void UpdateMaxRings()
    {
        int max = 1;
        foreach (WidgetView view in new[] { WidgetView.Ai, WidgetView.Pc, WidgetView.Custom })
            max = Math.Max(max, _views.Layout(view).Shown.Count(key => _available[Array.IndexOf(RingKeyOf, key)]));
        if (max == _maxRings) return;
        _maxRings = max;
        if (PreviewMode || !_hasWindowRect) LayOut(_windowHeightDip);
        else PositionOnPrimaryScreen();
    }

    /// Whether a ring is on screen right now (panel out, ring in the view and available).
    internal bool IsRingOnScreen(int index) => _expanded && _ringItems[index].Visibility == Visibility.Visible;

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Log.Trace("Edge", "settings button clicked");
        SettingsRequested?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Log.Trace("Edge", "close button clicked");
        CloseRequested?.Invoke();
    }

    /// Sets the tab without raising UsageViewChanged (used at start-up with the saved choice).
    internal void ApplyUsageView(UsageView view)
    {
        if (_usageView == view) return;
        _usageView = view;
        SyncUsageTabs();
        RefreshUsage();
    }

    /// The choice is global: both cards with tabs always show the same one.
    private void SyncUsageTabs()
    {
        _syncingTabs = true;
        bool total = _usageView == UsageView.Total;
        ClaudeSessionTab.IsChecked = CodexSessionTab.IsChecked = !total;
        ClaudeTotalTab.IsChecked = CodexTotalTab.IsChecked = total;
        _syncingTabs = false;
    }

    private void OnUsageViewChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTabs) return;
        UsageView view = sender == ClaudeTotalTab || sender == CodexTotalTab ? UsageView.Total : UsageView.Session;
        if (_usageView == view) return;
        Log.Trace("Edge", $"usage view -> {view}");
        _usageView = view;
        SyncUsageTabs();
        RefreshUsage();
        UsageViewChanged?.Invoke(view);
    }

    /// Re-applies the last snapshots so rings and card rows switch to the window of the selected tab.
    private void RefreshUsage()
    {
        if (_claude is not null) SetClaude(_claude);
        if (_codex is not null) SetCodex(_codex);
        if (_cursor is not null) SetCursor(_cursor);
    }

    /// Theme or language changed: repaint everything composed in code from the last readings.
    private void Reapply()
    {
        ApplyRingBrushes();
        SyncModeButton();
        SyncViewButton();
        if (_fps is FpsService.Sample fps) { _fps = null; SetFps(fps); }
        if (_gameMode is not null) SetGameMode(_gameMode);
        RefreshUsage();
        if (_openCode is not null) SetOpenCode(_openCode);
        if (_deepSeek is not null) SetDeepSeek(_deepSeek);
        if (_openRouter is not null) SetOpenRouter(_openRouter);
        if (_cpu is not null) SetCpu(_cpu);
        if (_gpu is not null) SetGpu(_gpu);
        if (_ram is not null) SetRam(_ram);
        SetRamNote(_ramNote);
        RefreshTimeTexts();
        CardContent.InvalidateMeasure();
        CenterPanel(animate: false);
        if (_cardVisible) PlaceCard(animate: false);
    }

    /// Resting colours of rings that have no reading yet; readings repaint their own ring.
    private void ApplyRingBrushes()
    {
        foreach (RingGauge ring in _rings) ring.RingBrush = Palette.Other;
        ClaudeRing.RingBrush = Palette.Claude;
        CodexRing.RingBrush = Palette.OpenAi;
        CpuRing.RingBrush = GpuRing.RingBrush = RamRing.RingBrush = Palette.Low;
    }

    private void OnClaudeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Trace("Edge", "claude ring clicked");
        ClaudeClicked?.Invoke();
    }

    private void OnCpuClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Trace("Edge", "cpu ring clicked");
        CpuClicked?.Invoke();
    }

    private void OnRamClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Trace("Edge", "ram ring clicked");
        RamClicked?.Invoke();
    }

    private void OnGameModeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Trace("Edge", "game mode ring clicked");
        GameModeClicked?.Invoke();
    }

    // ───────────────────────────── Accounts ─────────────────────────────

    /// The account tabs of a card: one per account (names[0] is the default one), only when there is more than one.
    internal void SetAccounts(AiProviderId provider, IReadOnlyList<string> names, int selected)
    {
        (Border? list, UniformGrid? grid) = provider switch
        {
            AiProviderId.Claude => (ClaudeAccounts, ClaudeAccountList),
            AiProviderId.Codex => (CodexAccounts, CodexAccountList),
            AiProviderId.Cursor => (CursorAccounts, CursorAccountList),
            _ => ((Border?)null, (UniformGrid?)null),
        };
        if (list is null || grid is null) return;
        _syncingAccounts = true;
        grid.Children.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            int index = i;
            var tab = new RadioButton
            {
                Style = (Style)FindResource("UsageTab"), Content = names[i], GroupName = provider + "Account",
                IsChecked = i == selected, ToolTip = names[i],
            };
            AutomationProperties.SetAutomationId(tab, $"{provider}Account{i}");
            tab.Checked += (_, _) =>
            {
                if (_syncingAccounts) return;
                Log.Trace("Edge", $"{provider} account -> {index}");
                AccountSelected?.Invoke(provider, index);
            };
            grid.Children.Add(tab);
        }
        list.Visibility = names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _syncingAccounts = false;
        if (_cardVisible) PlaceCard(animate: true);
    }

    // ───────────────────────────── FPS and Modo juego ─────────────────────────────

    /// FPS of the foreground program (or the desktop): the arc is the share of the screen's refresh rate.
    internal void SetFps(FpsService.Sample sample)
    {
        // Every second: nothing to repaint when what is shown would not change.
        if (_fps is FpsService.Sample shown && Math.Round(shown.Fps ?? -1) == Math.Round(sample.Fps ?? -1) && shown.Process == sample.Process
            && shown.Desktop == sample.Desktop && shown.RefreshHz == sample.RefreshHz && shown.Message == sample.Message) return;
        _fps = sample;
        FpsRefresh.Text = $"{sample.RefreshHz:0} Hz";
        if (sample.Fps is double fps)
        {
            double share = sample.RefreshHz > 0 ? Math.Clamp(fps / sample.RefreshHz, 0, 1) : 0;
            SolidColorBrush brush = share >= 0.75 ? Palette.Low : share >= 0.45 ? Palette.Medium : Palette.High;
            FpsRing.RingBrush = brush;
            // A new sample every second: set, never animated, or the layered window would repaint all the time.
            AnimateRing(FpsRing, share, 0);
            FpsLabel.Text = fps.ToString("0", Loc.Culture);
            FpsBar.Fill = brush;
            AnimateBar(FpsBar, share, 0);
            FpsValue.Text = Loc.Format("Value.Fps", fps.ToString("0", Loc.Culture));
            FpsFrameTime.Text = fps >= 1 ? Loc.Format("Value.Milliseconds", (1000 / fps).ToString("0.0", Loc.Culture)) : "--";
            FpsSource.Text = sample.Desktop ? Loc.Get("Fps.Desktop") : sample.Process ?? "--";
            FpsMetrics.Visibility = Visibility.Visible;
        }
        else
        {
            ClearRing(FpsRing, FpsLabel);
            FpsMetrics.Visibility = Visibility.Collapsed;
        }
        string? message = Loc.Message(sample.Message);
        FpsMessage.Text = message ?? string.Empty;
        FpsMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (_cardVisible && _activeRing == RingFps) PlaceCard(animate: false);
    }

    /// Modo juego: an empty ring while off, a full one while on; the card says what it did.
    internal void SetGameMode(GameModeSnapshot state)
    {
        _gameMode = state;
        GameModeRing.RingBrush = Palette.Low;
        AnimateRing(GameModeRing, state.Active ? 1 : 0, 400);
        GameModeLabel.Text = state.Busy ? "…" : state.Active ? "ON" : "OFF";
        GameModeItem.SetResourceReference(ToolTipProperty, !state.Allowed ? "Tip.GameModeSettings"
            : state.Active ? "Tip.GameModeOff" : "Tip.GameModeOn");
        GameModeState.Text = Loc.Get(state.Busy ? "Game.Applying" : !state.Allowed ? "Game.Disabled"
            : state.Active ? "Game.Active" : "Game.Inactive");

        GameModeResult? result = state.Result;
        if (state.Active && result is not null)
        {
            var parts = new List<string> { Loc.Format("Game.ClosedApps", result.ClosedApps), Loc.Format("Game.StoppedServices", result.StoppedServices) };
            if (result.PowerPlan is GamePowerPlan plan && plan != GamePowerPlan.Keep)
                parts.Add(Loc.Get(plan == GamePowerPlan.HighPerformance ? "Game.PlanHigh" : "Game.PlanBalanced"));
            if (result.GameBarOff) parts.Add(Loc.Get("Game.GameBarOff"));
            GameModeSummary.Text = string.Join(" · ", parts);
        }
        else GameModeSummary.Text = Loc.Get(state.Allowed ? "Game.Describe" : "Game.EnableFirst");

        IReadOnlyList<string> errors = result?.Errors ?? Array.Empty<string>();
        GameModeErrors.Text = string.Join("\n", errors.Take(4).Select(TranslateGameError));
        GameModeErrors.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GameModeHint.Text = Loc.Get(!state.Allowed ? "Game.HintSettings" : state.Active ? "Game.HintOff" : "Game.HintOn");
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// "No se pudo detener: WSearch" → the translated text, then the name as it is.
    private static string TranslateGameError(string error)
    {
        int colon = error.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? Loc.Message(error) ?? error : (Loc.Message(error[..colon]) ?? error[..colon]) + error[colon..];
    }

    /// Shown on the Claude card while the session is being renewed.
    internal void SetClaudeRenewing()
    {
        ClearRing(ClaudeRing, ClaudeLabel, "…");
        ClaudeMetrics.Visibility = Visibility.Collapsed;
        ClaudeTabs.Visibility = Visibility.Collapsed;
        ClaudeStale.Visibility = Visibility.Collapsed;
        ClaudeMessage.Text = Loc.Get("Value.Renewing");
        ClaudeMessage.Visibility = Visibility.Visible;
        ClaudeNote.Visibility = Visibility.Collapsed;
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// A line under the Claude card with the outcome of the last renewal ("Sesión renovada hasta las 18:53"), or
    /// nothing. It is kept across usage refreshes until the owner clears it.
    internal void SetClaudeNote(string? text)
    {
        ClaudeNote.Text = text ?? string.Empty;
        ClaudeNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// Vertically centres the panel; its height changes when a ring is shown or hidden.
    private void CenterPanel(bool animate)
    {
        // Invalidate every level explicitly: a bare Measure() on an ancestor returns a cached size when only a
        // descendant changed.
        PanelStack.InvalidateMeasure();
        PanelRoot.InvalidateMeasure();
        EdgePanel.InvalidateMeasure();
        EdgePanel.Measure(new Size(PanelWidth, double.PositiveInfinity));
        _panelTop = Math.Round((_availableHeight - EdgePanel.DesiredSize.Height) / 2);
        Animate(EdgePanel, Canvas.TopProperty, _panelTop, animate ? PanelMs : 0);
    }

    // ───────────────────────────── Scale ─────────────────────────────

    /// "uiScale" from the settings: a fixed scale, or null for automatic. Takes effect immediately.
    internal void ApplyScale(double? uiScale)
    {
        _uiScale = uiScale is double s ? Math.Clamp(s, SettingsStore.MinUiScale, SettingsStore.MaxUiScale) : null;
        if (PreviewMode || !_hasWindowRect) LayOut(_windowHeightDip);
        else PositionOnPrimaryScreen();
    }

    /// Snapshot support: lays the window out as if the monitor's work area were this many DIPs tall.
    internal void PreviewWorkArea(double workHeightDip) => LayOut(workHeightDip);

    internal double Scale => _scale;

    /// Picks the scale for a work area of the given height (DIPs) and lays the canvas out in design units.
    /// Automatic: proportional to the work area, limited to MinAutoScale–MaxAutoScale so the vectors stay crisp.
    /// Either way it never exceeds what fits the panel with the rings of its largest view, so nothing is ever cut off.
    private void LayOut(double workHeightDip)
    {
        double preferred = _uiScale ?? Math.Clamp(workHeightDip / ReferenceWorkHeight, MinAutoScale, MaxAutoScale);
        double fit = (workHeightDip - 2 * ScreenMargin) / FullPanelHeight();
        _scale = Math.Round(Math.Max(MinFitScale, Math.Min(preferred, fit)), 3);
        _windowHeightDip = workHeightDip;
        _availableHeight = workHeightDip / _scale;

        RootScale.ScaleX = RootScale.ScaleY = _scale;
        Root.Width = WindowWidthDip;
        Root.Height = _availableHeight;
        Width = WindowWidthDip * _scale;
        Height = workHeightDip;

        Strip.Width = StripWidth / _scale;
        Strip.Height = Math.Min(StripHeight, _availableHeight);
        Canvas.SetLeft(Strip, WindowWidthDip - Strip.Width);
        Canvas.SetTop(Strip, (_availableHeight - Strip.Height) / 2);

        LayOutButtons();
        CenterPanel(animate: false);
        if (_cardVisible) PlaceCard(animate: false);
        Log.Trace("Edge", $"scale {_scale} (preferred {preferred:0.###}, fit {fit:0.###}) for {workHeightDip:0} DIP");
    }

    /// Height of the panel (design units) with as many rings as the largest view shows (UpdateMaxRings), whatever is
    /// visible right now, so the scale does not jump when switching views.
    private double FullPanelHeight()
    {
        var saved = new Visibility[_ringItems.Length];
        int count = Math.Max(1, _maxRings);
        for (int i = 0; i < _ringItems.Length; i++)
        {
            saved[i] = _ringItems[i].Visibility;
            _ringItems[i].Visibility = RingStack.Children.IndexOf(_ringItems[i]) < count ? Visibility.Visible : Visibility.Collapsed;
        }
        LayOutButtons();
        PanelStack.InvalidateMeasure();
        PanelRoot.InvalidateMeasure();
        EdgePanel.InvalidateMeasure();
        EdgePanel.Measure(new Size(PanelWidth, double.PositiveInfinity));
        double height = EdgePanel.DesiredSize.Height;
        for (int i = 0; i < _ringItems.Length; i++) _ringItems[i].Visibility = saved[i];
        return height;
    }

    /// Four buttons, two by two (four in a row would be under 20 DIP at any scale).
    private void LayOutButtons()
    {
        ModeButton.Margin = new Thickness(0);
        ViewButton.Margin = new Thickness(ButtonGap, 0, 0, 0);
        SettingsButton.Margin = new Thickness(0, ButtonGap, 0, 0);
        CloseButton.Margin = new Thickness(ButtonGap, ButtonGap, 0, 0);
        foreach (Button button in new[] { ModeButton, ViewButton, SettingsButton, CloseButton })
        {
            button.Width = button.Height = ButtonDiameter;
            if (button.Content is FrameworkElement icon) icon.Width = icon.Height = Math.Round(ButtonDiameter * 0.5);
        }
    }

    // ───────────────────────────── Data ─────────────────────────────

    internal void SetClaude(ClaudeSnapshot snapshot)
    {
        _claude = snapshot;
        SetRingVisible(RingClaude, !snapshot.Hidden);
        if (snapshot.Hidden) return;
        SetPlan(ClaudePlan, ClaudePlanText, snapshot.Plan);

        if (snapshot.Session is UsageWindow session)
        {
            // "Total" shows the weekly limit; "--" when the response carried none.
            bool total = _usageView == UsageView.Total;
            if ((total ? snapshot.Weekly : session) is UsageWindow ringWindow)
                SetUsageRing(ClaudeRing, ClaudeLabel, Palette.Claude, ringWindow.Percent);
            else
                ClearRing(ClaudeRing, ClaudeLabel);

            SetPercentBar(SessionBar, session.Percent);
            SessionValue.Text = Fmt.Used(session.Percent);

            if (snapshot.Weekly is UsageWindow weekly)
            {
                SetPercentBar(WeeklyBar, weekly.Percent);
                WeeklyValue.Text = Fmt.Used(weekly.Percent);
            }
            else
            {
                ClearBar(WeeklyBar);
                WeeklyValue.Text = "--";
            }

            ClaudeSessionRow.Visibility = total ? Visibility.Collapsed : Visibility.Visible;
            ClaudeWeeklyRow.Visibility = total ? Visibility.Visible : Visibility.Collapsed;

            if (snapshot.Spent is Money spent && spent.Amount > 0)
            {
                ClaudeSpendValue.Text = Fmt.Amount(spent);
                ClaudeSpendRow.Visibility = Visibility.Visible;
            }
            else
            {
                ClaudeSpendRow.Visibility = Visibility.Collapsed;
            }

            ClaudeTabs.Visibility = Visibility.Visible;
            ClaudeMetrics.Visibility = Visibility.Visible;
            ClaudeMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClearRing(ClaudeRing, ClaudeLabel);
            ClaudeTabs.Visibility = Visibility.Collapsed;
            ClaudeMetrics.Visibility = Visibility.Collapsed;
            ClaudeMessage.Text = Loc.Message(snapshot.Message) ?? Loc.Get("Value.NoData");
            ClaudeMessage.Visibility = Visibility.Visible;
        }

        RefreshTimeTexts();
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetCodex(CodexSnapshot snapshot)
    {
        _codex = snapshot;
        SetRingVisible(RingCodex, !snapshot.Hidden);
        if (snapshot.Hidden) return;
        SetPlan(CodexPlan, CodexPlanText, snapshot.Plan);

        if (snapshot.Primary is CodexWindow primary)
        {
            CodexWindow shown = CodexRingWindow(primary, snapshot.Secondary);
            _codexShown = shown;
            SetUsageRing(CodexRing, CodexLabel, Palette.OpenAi, shown.Percent);

            CodexWindowLabel.Text = Fmt.WindowLabel(shown.Length);
            SetPercentBar(CodexBar, shown.Percent);
            CodexValue.Text = Fmt.Used(shown.Percent);

            if (snapshot.Credits is CodexCredits credits)
            {
                CodexCreditsValue.Text = credits.Unlimited ? Loc.Get("Value.Unlimited")
                    : Loc.Format("Value.Credits", credits.Balance!.Value.ToString("N2", Loc.Culture));
                CodexCreditsRow.Visibility = Visibility.Visible;
            }
            else
            {
                CodexCreditsRow.Visibility = Visibility.Collapsed;
            }

            CodexTabs.Visibility = snapshot.Secondary is null ? Visibility.Collapsed : Visibility.Visible;
            CodexMetrics.Visibility = Visibility.Visible;
            CodexMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            _codexShown = null;
            ClearRing(CodexRing, CodexLabel);
            CodexTabs.Visibility = Visibility.Collapsed;
            CodexMetrics.Visibility = Visibility.Collapsed;
            CodexMessage.Text = Loc.Message(snapshot.Message) ?? Loc.Get("Value.NoData");
            CodexMessage.Visibility = Visibility.Visible;
        }

        RefreshTimeTexts();
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetCursor(CursorSnapshot snapshot)
    {
        _cursor = snapshot;
        SetRingVisible(RingCursor, !snapshot.Hidden);
        if (snapshot.Hidden) return;
        SetPlan(CursorPlan, CursorPlanText, snapshot.Plan);

        // Cursor only has the monthly billing cycle, so both tabs show it.
        if (snapshot.Cycle is UsageWindow cycle)
        {
            SetUsageRing(CursorRing, CursorLabel, Palette.Other, cycle.Percent);
            SetPercentBar(CursorCycleBar, cycle.Percent);
            CursorCycleValue.Text = Fmt.Used(cycle.Percent);
            // On-demand: what has been spent beyond the plan, "3,21 USD de 50,00 USD" with a bar when there is a limit.
            if (snapshot.OnDemandSpent is Money onDemandSpent)
            {
                if (snapshot.OnDemand is UsageWindow onDemand && snapshot.OnDemandLimit is Money onDemandLimit)
                {
                    SetPercentBar(CursorOnDemandBar, onDemand.Percent);
                    CursorOnDemandBar.Visibility = Visibility.Visible;
                    CursorOnDemandValue.Text = Loc.Format("Value.Of", Fmt.Amount(onDemandSpent), Fmt.Amount(onDemandLimit));
                }
                else
                {
                    CursorOnDemandBar.Visibility = Visibility.Collapsed;
                    CursorOnDemandValue.Text = Fmt.Amount(onDemandSpent);
                }
                CursorOnDemandRow.Visibility = Visibility.Visible;
            }
            else CursorOnDemandRow.Visibility = Visibility.Collapsed;
            CursorMetrics.Visibility = Visibility.Visible;
            CursorMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClearRing(CursorRing, CursorLabel);
            CursorMetrics.Visibility = Visibility.Collapsed;
            CursorMessage.Text = Loc.Message(snapshot.Message) ?? Loc.Get("Value.NoData");
            CursorMessage.Visibility = Visibility.Visible;
        }

        RefreshTimeTexts();
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// "Sesión" → the shorter window, "Total" → the longer one. With a single window (e.g. the monthly one of the
    /// Go plan) both tabs show it.
    private CodexWindow CodexRingWindow(CodexWindow primary, CodexWindow? secondary)
    {
        if (secondary is null) return primary;
        bool primaryShorter = (primary.Length ?? TimeSpan.Zero) <= (secondary.Length ?? TimeSpan.MaxValue);
        CodexWindow shorter = primaryShorter ? primary : secondary;
        CodexWindow longer = primaryShorter ? secondary : primary;
        return _usageView == UsageView.Total ? longer : shorter;
    }

    private static void SetPlan(Border badge, TextBlock text, string? plan)
    {
        string? label = Fmt.Plan(plan);
        text.Text = label ?? string.Empty;
        badge.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetOpenCode(OpenCodeSnapshot snapshot)
    {
        _openCode = snapshot;
        SetRingVisible(RingOpenCode, !snapshot.Hidden);
        if (snapshot.Hidden) return;
        if (snapshot.Message is null && snapshot.CostUsd is decimal cost)
        {
            decimal total = (decimal)snapshot.TokensIn + snapshot.TokensOut + snapshot.TokensReasoning
                + snapshot.TokensCacheRead + snapshot.TokensCacheWrite;
            OpenCodeLabel.Text = CompactCount(total);
            AnimateRing(OpenCodeRing, 0, 300);
            OpenCodeInput.Text = snapshot.TokensIn.ToString("N0", Loc.Culture);
            OpenCodeOutput.Text = snapshot.TokensOut.ToString("N0", Loc.Culture);
            OpenCodeReasoning.Text = snapshot.TokensReasoning.ToString("N0", Loc.Culture);
            OpenCodeCache.Text = $"{snapshot.TokensCacheRead.ToString("N0", Loc.Culture)} / {snapshot.TokensCacheWrite.ToString("N0", Loc.Culture)}";
            OpenCodeCost.Text = Fmt.Amount(new Money(cost, "USD"));
            OpenCodeMetrics.Visibility = Visibility.Visible;
            OpenCodeMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            OpenCodeLabel.Text = "--";
            AnimateRing(OpenCodeRing, 0, 300);
            OpenCodeMetrics.Visibility = Visibility.Collapsed;
            OpenCodeMessage.Text = Loc.Message(snapshot.Message) ?? Loc.Get("Value.NoData");
            OpenCodeMessage.Visibility = Visibility.Visible;
        }
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetDeepSeek(DeepSeekSnapshot snapshot)
    {
        _deepSeek = snapshot;
        SetRingVisible(RingDeepSeek, !snapshot.Hidden);
        if (snapshot.Hidden) return;
        if (snapshot.Balance is Money balance)
        {
            DeepSeekLabel.Text = CompactMoney(balance);
            AnimateRing(DeepSeekRing, 0, 300);
            DeepSeekBalance.Text = Fmt.Amount(balance);
            DeepSeekMetrics.Visibility = Visibility.Visible;
            DeepSeekMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            DeepSeekLabel.Text = "--";
            AnimateRing(DeepSeekRing, 0, 300);
            DeepSeekMetrics.Visibility = Visibility.Collapsed;
            DeepSeekMessage.Text = Loc.Message(snapshot.Message) ?? Loc.Get("Value.NoData");
            DeepSeekMessage.Visibility = Visibility.Visible;
        }
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetOpenRouter(OpenRouterSnapshot snapshot)
    {
        _openRouter = snapshot;
        SetRingVisible(RingOpenRouter, !snapshot.Hidden);
        if (snapshot.Hidden) return;
        if (snapshot.Message is null)
        {
            if (snapshot.LimitUsd is decimal limit && limit > 0 && snapshot.RemainingUsd is decimal remaining)
            {
                double percent = (double)Math.Clamp(snapshot.UsageUsd / limit * 100, 0, 100);
                SetUsageRing(OpenRouterRing, OpenRouterLabel, Palette.Other, percent);
                OpenRouterMetricLabel.Text = Loc.Get("Row.LimitUsage");
                OpenRouterUsage.Text = Loc.Format("Value.Of", Fmt.Amount(new Money(snapshot.UsageUsd, "USD")), Fmt.Amount(new Money(limit, "USD")));
                OpenRouterRemainingLabel.Visibility = Visibility.Visible;
                OpenRouterRemaining.Visibility = Visibility.Visible;
                OpenRouterRemaining.Text = Fmt.Amount(new Money(remaining, "USD"));
            }
            else
            {
                ClearRing(OpenRouterRing, OpenRouterLabel, CompactMoney(new Money(snapshot.UsageUsd, "USD")));
                OpenRouterMetricLabel.Text = Loc.Get("Row.TotalSpend");
                OpenRouterUsage.Text = Fmt.Amount(new Money(snapshot.UsageUsd, "USD"));
                OpenRouterRemainingLabel.Visibility = Visibility.Collapsed;
                OpenRouterRemaining.Visibility = Visibility.Collapsed;
            }
            OpenRouterMetrics.Visibility = Visibility.Visible;
            OpenRouterMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClearRing(OpenRouterRing, OpenRouterLabel);
            OpenRouterMetrics.Visibility = Visibility.Collapsed;
            OpenRouterMessage.Text = Loc.Message(snapshot.Message);
            OpenRouterMessage.Visibility = Visibility.Visible;
        }
        if (_cardVisible) PlaceCard(animate: true);
    }

    private static string CompactCount(decimal value) => value >= 1_000_000 ? $"{value / 1_000_000m:0.#}M"
        : value >= 10_000 ? $"{value / 1_000m:0.#}K" : value.ToString("N0", Loc.Culture);

    private static string CompactMoney(Money money)
    {
        string symbol = money.Currency == "USD" ? "$" : money.Currency == "CNY" ? "¥" : money.Currency;
        return money.Amount >= 1000 ? $"{symbol}{money.Amount / 1000m:0.#}k" : $"{symbol}{money.Amount:0.##}";
    }

    internal void SetCpu(CpuSnapshot snapshot)
    {
        _cpu = snapshot;
        if (snapshot.Temperature is double temperature)
        {
            ResetTemperatureLabel(CpuLabel);
            SetTemperatureRing(CpuRing, CpuLabel, temperature);
            SetTemperatureBar(TempBar, temperature);
            TempValue.Text = Fmt.Celsius(temperature);
        }
        else
        {
            if (snapshot.Message == HardwareSensorService.PawnIoMissingMessage) { ClearRing(CpuRing, CpuLabel, Loc.Get("Ring.InstallPawnIo")); SetPawnIoLabel(CpuLabel); }
            else { ResetTemperatureLabel(CpuLabel); ClearRing(CpuRing, CpuLabel); }
            ClearBar(TempBar);
            TempValue.Text = "--";
        }

        if (snapshot.MaxTemperature is double max)
        {
            SetTemperatureBar(MaxBar, max);
            MaxValue.Text = Fmt.Celsius(max);
        }
        else
        {
            ClearBar(MaxBar);
            MaxValue.Text = "--";
        }

        if (snapshot.Load is double load)
        {
            SetPercentBar(LoadBar, load);
            LoadValue.Text = Loc.Format("Value.InUse", Fmt.Percent(load));
        }
        else
        {
            ClearBar(LoadBar);
            LoadValue.Text = "--";
        }

        CpuTitle.Text = ShortHardwareName(snapshot.Name, "CPU");
        string? message = Loc.Message(snapshot.Message);
        if (CpuMessage.Text != (message ?? string.Empty) || (CpuMessage.Visibility == Visibility.Visible) != (message is not null))
        {
            CpuMessage.Text = message ?? string.Empty;
            CpuMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
            CardContent.InvalidateMeasure();
        }

        RefreshTimeTexts();
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetGpu(GpuSnapshot snapshot)
    {
        _gpu = snapshot;
        SetRingVisible(RingGpu, snapshot.Detected);
        if (!snapshot.Detected) return;

        if (snapshot.Temperature is double temperature)
        {
            ResetTemperatureLabel(GpuLabel);
            SetTemperatureRing(GpuRing, GpuLabel, temperature);
            SetTemperatureBar(GpuTempBar, temperature);
            GpuTempValue.Text = Fmt.Celsius(temperature);
        }
        else
        {
            if (snapshot.Message == HardwareSensorService.PawnIoMissingMessage) { ClearRing(GpuRing, GpuLabel, Loc.Get("Ring.InstallPawnIo")); SetPawnIoLabel(GpuLabel); }
            else { ResetTemperatureLabel(GpuLabel); ClearRing(GpuRing, GpuLabel); }
            ClearBar(GpuTempBar);
            GpuTempValue.Text = "--";
        }

        if (snapshot.Load is double load)
        {
            SetPercentBar(GpuLoadBar, load);
            GpuLoadValue.Text = Loc.Format("Value.InUse", Fmt.Percent(load));
        }
        else
        {
            ClearBar(GpuLoadBar);
            GpuLoadValue.Text = "--";
        }

        if (snapshot.MemoryUsedMb is double used)
        {
            if (snapshot.MemoryTotalMb is double total && total > 0)
            {
                SetPercentBar(GpuMemoryBar, used / total * 100);
                GpuMemoryBar.Visibility = Visibility.Visible;
                GpuMemoryValue.Text = Loc.Format("Value.Of", Fmt.Megabytes(used), Fmt.Megabytes(total));
            }
            else
            {
                GpuMemoryBar.Visibility = Visibility.Collapsed;
                GpuMemoryValue.Text = Fmt.Megabytes(used);
            }
            GpuMemoryRow.Visibility = Visibility.Visible;
        }
        else
        {
            GpuMemoryRow.Visibility = Visibility.Collapsed;
        }

        GpuTitle.Text = ShortHardwareName(snapshot.Name, "GPU");
        string? message = Loc.Message(snapshot.Message);
        if (GpuMessage.Text != (message ?? string.Empty) || (GpuMessage.Visibility == Visibility.Visible) != (message is not null))
        {
            GpuMessage.Text = message ?? string.Empty;
            GpuMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
            CardContent.InvalidateMeasure();
        }

        if (_cardVisible) PlaceCard(animate: true);
    }

    /// Physical memory: the ring shows the load Windows reports (low / medium / high by level, red above 85 %).
    internal void SetRam(RamSnapshot snapshot)
    {
        _ram = snapshot;
        if (snapshot.Message is null)
        {
            RamRing.RingBrush = Palette.ForPercent(snapshot.Percent);
            AnimateRing(RamRing, Fraction(snapshot.Percent), 600);
            RamLabel.Text = Fmt.Percent(snapshot.Percent);
            SetLabelAlert(RamLabel, Palette.IsAlert(snapshot.Percent));
            SetPercentBar(RamBar, snapshot.Percent);
            RamUsedValue.Text = Loc.Format("Value.Of", Fmt.Gigabytes(snapshot.UsedBytes), Fmt.Gigabytes(snapshot.TotalBytes));
            RamCachedValue.Text = snapshot.CachedBytes is ulong cached ? Fmt.Gigabytes(cached) : "--";
            RamCommittedValue.Text = snapshot.CommittedBytes is ulong committed && snapshot.CommitLimitBytes is ulong limit
                ? Loc.Format("Value.Of", Fmt.Gigabytes(committed), Fmt.Gigabytes(limit)) : "--";
            RamMetrics.Visibility = Visibility.Visible;
            RamMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClearRing(RamRing, RamLabel);
            RamMetrics.Visibility = Visibility.Collapsed;
            RamMessage.Text = Loc.Message(snapshot.Message);
            RamMessage.Visibility = Visibility.Visible;
        }
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// Bottom line of the RAM card: the outcome of the last clean-up, or (null) the hint of what a click does.
    internal void SetRamNote(string? text)
    {
        _ramNote = text;
        RamNote.Text = text ?? Loc.Get("Value.RamHint");
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// Whether a ring has something to show (provider installed, GPU detected); its view decides the rest.
    private void SetRingVisible(int index, bool visible)
    {
        if (_available[index] == visible) return;
        Log.Trace("Edge", $"ring {index} {(visible ? "available" : "unavailable")}");
        _available[index] = visible;
        ApplyRingVisibility();
    }

    private void RefreshTimeTexts()
    {
        var now = DateTimeOffset.Now;
        ClaudeHeaderReset.Text = _claude?.Session is UsageWindow session ? Fmt.Reset(session.ResetsAt, now) : string.Empty;
        if (_claude is { Session: not null, StaleSince: DateTimeOffset readAt })
        {
            ClaudeStale.Text = Loc.Format("Claude.Stale", Math.Max(1, (int)Math.Round((now - readAt).TotalMinutes)));
            ClaudeStale.Visibility = Visibility.Visible;
        }
        else ClaudeStale.Visibility = Visibility.Collapsed;
        WeeklyReset.Text = _claude?.Weekly is UsageWindow weekly ? Fmt.Reset(weekly.ResetsAt, now) : string.Empty;
        CodexReset.Text = _codexShown is CodexWindow shown ? Fmt.Reset(shown.ResetsAt, now) : string.Empty;
        CursorHeaderReset.Text = _cursor?.Cycle is UsageWindow cursorCycle ? Fmt.Reset(cursorCycle.ResetsAt, now) : string.Empty;
        CpuSince.Text = Loc.Format("Card.Since", _sessionStart);
    }

    /// Usage ring: the provider's colour; above Palette.AlertPercent both the arc and the percentage turn red.
    private void SetUsageRing(RingGauge ring, TextBlock label, SolidColorBrush brand, double percent)
    {
        ring.RingBrush = Palette.ForRing(brand, percent);
        AnimateRing(ring, Fraction(percent), 600);
        label.Text = Fmt.Percent(percent);
        SetLabelAlert(label, Palette.IsAlert(percent));
    }

    /// Temperature ring: low / medium / red by band, and the reading turns red with the arc.
    private void SetTemperatureRing(RingGauge ring, TextBlock label, double celsius)
    {
        SolidColorBrush brush = Palette.ForTemperature(celsius);
        ring.RingBrush = brush;
        AnimateRing(ring, Fraction(celsius), 600);
        label.Text = Fmt.Celsius(celsius);
        SetLabelAlert(label, brush == Palette.Red);
    }

    private void ClearRing(RingGauge ring, TextBlock label, string text = "--")
    {
        AnimateRing(ring, 0, 300);
        label.Text = text;
        SetLabelAlert(label, false);
    }

    /// Red in alert; otherwise back to the RingLabel style, which follows the theme's text colour.
    private static void SetLabelAlert(TextBlock label, bool alert)
    {
        if (alert) label.Foreground = Palette.Red;
        else label.ClearValue(TextBlock.ForegroundProperty);
    }

    private static void SetPawnIoLabel(TextBlock label)
    {
        label.FontSize = 7;
        label.TextWrapping = TextWrapping.Wrap;
        label.MaxWidth = 50;
        label.Margin = new Thickness(0);
    }

    private static void ResetTemperatureLabel(TextBlock label)
    {
        label.ClearValue(TextBlock.FontSizeProperty);
        label.TextWrapping = TextWrapping.NoWrap;
        label.MaxWidth = double.PositiveInfinity;
        label.ClearValue(MarginProperty);
    }

    private void SetPercentBar(LinearBar bar, double percent)
    {
        bar.Fill = Palette.ForPercent(percent);
        AnimateBar(bar, Fraction(percent), 500);
    }

    private void SetTemperatureBar(LinearBar bar, double celsius)
    {
        bar.Fill = Palette.ForTemperature(celsius);
        AnimateBar(bar, Fraction(celsius), 500);
    }

    private void ClearBar(LinearBar bar) => AnimateBar(bar, 0, 300);

    private void AnimateRing(RingGauge ring, double to, int milliseconds) =>
        AnimateReading(ring, RingGauge.ValueProperty, to, milliseconds, ring.IsVisible);

    /// Bars live in the card: only the one on screen is animated.
    private void AnimateBar(LinearBar bar, double to, int milliseconds) =>
        AnimateReading(bar, LinearBar.ValueProperty, to, milliseconds, _cardVisible && bar.IsVisible);

    /// A new reading on a ring or bar: animated only while it is on screen and moves by MinAnimatedChange or more,
    /// otherwise set in one step (and not repainted at all when the value is the same).
    private void AnimateReading(UIElement target, DependencyProperty property, double to, int milliseconds, bool onScreen)
    {
        if (!onScreen || Math.Abs((double)target.GetValue(property) - to) < MinAnimatedChange) milliseconds = 0;
        Animate(target, property, to, milliseconds);
    }

    private static double Fraction(double percentOrCelsius) => Math.Clamp(percentOrCelsius / 100.0, 0, 1);

    private static string ShortHardwareName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        foreach (string vendor in new[] { "Intel ", "AMD ", "NVIDIA " })
            if (name.StartsWith(vendor, StringComparison.Ordinal)) return name[vendor.Length..];
        return name;
    }

    // ─────────────────────────── Pointer polling ───────────────────────────

    private void OnPointerTick(object? sender, EventArgs e)
    {
        if (_tickClock.ElapsedMilliseconds > 400)
            Log.Trace("Edge", $"UI stall: {_tickClock.ElapsedMilliseconds} ms between pointer ticks");
        _tickClock.Restart();

        if (!GetCursorPos(out POINT cursor)) return;
        if (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y)
        {
            _lastCursor = cursor;
            _pointerStill.Restart();
        }

        // Poll quickly only when the pointer is over the strip, panel or visible card; ignore the empty window area.
        bool near = !_hasWindowRect || NearInteractiveArea(cursor);
        SetPollInterval(near ? NearPoll : FarPoll);
        if (!near)
        {
            if (_expanded) HandleCursorAway();
            // Pinned and at rest: nothing to watch until the pointer comes back, and then the window itself reports
            // it (it takes mouse input while expanded, see OnWindowMouseMove).
            if (_mode == PanelMode.Pinned && _expanded && !_cardVisible && _hoveredRing < 0) PausePointerWatch();
            else if (!_expanded && _pointerStill.Elapsed >= PointerRestBeforeSleep) SleepUntilPointerMoves();
            return;
        }

        var screen = new Point(cursor.X, cursor.Y);

        if (!_expanded)
        {
            // Mouse button held = probably dragging a window to the edge; don't pop the panel over it.
            if (_mode == PanelMode.Auto && Contains(Strip, screen) && !IsAnyMouseButtonDown()) Expand();
            return;
        }

        int ring = -1;
        for (int i = 0; i < _ringItems.Length; i++)
        {
            if (_ringItems[i].Visibility == Visibility.Visible && Contains(_ringItems[i], screen))
            {
                ring = i;
                break;
            }
        }

        if (ring != _hoveredRing)
        {
            if (_hoveredRing >= 0) ScaleRing(_hoveredRing, 1.0);
            _hoveredRing = ring;
            if (ring >= 0)
            {
                Log.Trace("Edge", $"hover ring {ring} at {cursor.X},{cursor.Y}");
                ScaleRing(ring, RingHoverScale);
                ShowCard(ring);
            }
        }

        // In auto mode the strip zone counts as inside too; otherwise a cursor parked on the strip above or
        // below the (shorter) panel would expand/collapse in a loop.
        bool inside = Contains(EdgePanel, screen)
                      || (_cardVisible && Contains(Card, screen))
                      || (_mode == PanelMode.Auto && Contains(Strip, screen));
        if (inside) _outside.Reset();
        else HandleCursorAway();
    }

    /// The cursor is off the panel and off the card: drop any ring hover, then collapse once the grace period
    /// has passed — or, when pinned, only close the card.
    private void HandleCursorAway()
    {
        if (_hoveredRing >= 0)
        {
            ScaleRing(_hoveredRing, 1.0);
            _hoveredRing = -1;
        }

        if (!_outside.IsRunning) _outside.Start();
        if (_outside.Elapsed < CollapseGrace) return;

        if (_mode == PanelMode.Auto)
        {
            Log.Trace("Edge", $"outside panel and card for {_outside.ElapsedMilliseconds} ms");
            Collapse();
        }
        else if (_cardVisible)
        {
            // Pinned: the panel stays, only the card goes away.
            Log.Trace("Edge", $"pinned: outside panel and card for {_outside.ElapsedMilliseconds} ms");
            HideCard();
        }
    }

    /// Assigning Interval restarts the timer, so only ever write it when the cadence actually changes.
    private void SetPollInterval(TimeSpan interval)
    {
        if (PreviewMode || _pointerWatch.Interval == interval) return;
        _pointerWatch.Interval = interval;
    }

    private void PausePointerWatch()
    {
        if (!_pointerWatch.IsEnabled) return;
        _pointerWatch.Stop();
        Log.Trace("Edge", "pointer watch paused");
    }

    /// The layered window only receives the pointer over its painted pixels (panel, visible card), and never while
    /// click-through (collapsed), so this fires exactly when the paused poll has something to track again.
    private void OnWindowMouseMove(object sender, System.Windows.Input.MouseEventArgs e) => ResumePointerWatch();

    /// Collapsed with the pointer at rest: the window is click-through, so it gets no mouse messages, but raw input
    /// (mouse, pen, touch) reaches it in the background; the next one restarts the poll. If raw input cannot be
    /// registered the poll simply keeps running.
    private void SleepUntilPointerMoves()
    {
        if (!_pointerWatch.IsEnabled) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !WatchRawPointerInput(hwnd, true)) return;
        _wakeOnInput = true;
        _pointerWatch.Stop();
        Log.Trace("Edge", "pointer watch asleep until input");
    }

    private void ResumePointerWatch()
    {
        if (_wakeOnInput)
        {
            _wakeOnInput = false;
            WatchRawPointerInput(IntPtr.Zero, false);
        }
        if (PreviewMode || !_hasWindowRect || _pointerWatch.IsEnabled) return;
        Log.Trace("Edge", "pointer watch resumed");
        _tickClock.Restart();
        _pointerStill.Restart();
        _pointerWatch.Start();
    }

    private static bool Contains(FrameworkElement element, Point screen)
    {
        if (PresentationSource.FromVisual(element) is null) return false;
        Point local = element.PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X < element.ActualWidth && local.Y < element.ActualHeight;
    }

    private bool NearInteractiveArea(POINT point)
    {
        if (!_expanded) return _mode == PanelMode.Auto && Contains(Strip, new Point(point.X, point.Y));
        return Contains(EdgePanel, new Point(point.X, point.Y))
               || (_cardVisible && Contains(Card, new Point(point.X, point.Y)));
    }

    // ─────────────────────────── Expand / collapse ───────────────────────────

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        Log.Trace("Edge", $"expand ({_mode})");

        SyncModeButton();
        if (!PreviewMode)
        {
            SetClickThrough(false);
            ReassertTopmost();
            _outside.Reset();
            ResumePointerWatch();
        }

        Animate(PanelShift, TranslateTransform.XProperty, 0, PanelMs);
        Animate(Strip, OpacityProperty, 0, 150);
        ExpandedChanged?.Invoke(true);
        VisibleRingsChanged?.Invoke();
    }

    /// The reverse of Expand: same 250 ms, the cubic curve mirrored in time (ease-in). The animations must start
    /// before _expanded is cleared: Animate jumps straight to the end for a collapsed panel.
    private void Collapse()
    {
        if (!_expanded) return;
        Log.Trace("Edge", "collapse");

        _outside.Reset();
        _hoveredRing = -1;
        if (!PreviewMode) SetClickThrough(true);

        HideCard();
        for (int i = 0; i < _rings.Length; i++) ScaleRing(i, 1.0);
        Animate(PanelShift, TranslateTransform.XProperty, PanelWidth, PanelMs, ease: EaseIn);
        Animate(Strip, OpacityProperty, 1, PanelMs, ease: EaseIn);
        _expanded = false;
        // Click-through from now on: only the poll can see the pointer reach the strip.
        ResumePointerWatch();
        ExpandedChanged?.Invoke(false);
        VisibleRingsChanged?.Invoke();
    }

    /// Slides the panel out before the application quits (the close button, "Salir"), then calls done.
    internal void SlideOut(Action done)
    {
        if (!_expanded || !AnimationsEnabled || PreviewMode) { done(); return; }
        Collapse();
        var wait = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(PanelMs) };
        wait.Tick += (_, _) => { wait.Stop(); done(); };
        wait.Start();
    }

    // ───────────────────────────── Rings & card ─────────────────────────────

    private void ScaleRing(int index, double scale)
    {
        var transform = (ScaleTransform)_rings[index].RenderTransform;
        Animate(transform, ScaleTransform.ScaleXProperty, scale, RingHoverMs);
        Animate(transform, ScaleTransform.ScaleYProperty, scale, RingHoverMs);
    }

    private void ShowCard(int index)
    {
        bool switching = _cardVisible && _activeRing != index;
        if (_cardVisible && !switching) return;
        Log.Trace("Edge", $"show card {index} (switching={switching})");

        _activeRing = index;
        for (int i = 0; i < _cards.Length; i++)
            _cards[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        RefreshTimeTexts();

        if (switching)
        {
            // Slide from the previous ring to the new one — the card never disappears.
            PlaceCard(animate: true);
        }
        else
        {
            PlaceCard(animate: false);
            _cardVisible = true;
            // The card takes clicks (its tabs) only while it is on screen.
            Card.IsHitTestVisible = true;
            _timeTextTimer.Start();
            Animate(CardShift, TranslateTransform.XProperty, 0, 220, from: 10);
            Animate(Card, OpacityProperty, 1, 180);
        }
    }

    private void HideCard()
    {
        if (!_cardVisible) return;
        Log.Trace("Edge", "hide card");
        _cardVisible = false;
        Card.IsHitTestVisible = false;
        _timeTextTimer.Stop();
        _activeRing = -1;
        Animate(Card, OpacityProperty, 0, 150);
    }

    /// Centres the card on the active ring (clamped to the window) and points the beak at the ring.
    private void PlaceCard(bool animate)
    {
        if (_activeRing < 0) return;

        // Measure only the card, never UpdateLayout (that would remeasure the whole layered window on every sample).
        // Always unconstrained: a layout pass measures it within the card's current height (0 before the first show),
        // and Measure itself is a no-op while neither the content nor the constraint changed.
        CardContent.Measure(new Size(CardBodyWidth, double.PositiveInfinity));
        double height = Math.Ceiling(CardContent.DesiredSize.Height);

        // Measured against the panel's target top, so the card aims at where the ring ends up while the panel is
        // still re-centring after a ring appeared or disappeared.
        FrameworkElement ring = _rings[_activeRing];
        double ringCenter = _panelTop + ring.TranslatePoint(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2), EdgePanel).Y;

        double maxTop = Math.Max(CardVerticalRoom, _availableHeight - CardVerticalRoom - height);
        double top = Math.Min(Math.Max(ringCenter - height / 2, CardVerticalRoom), maxTop);
        double beakCenter = ringCenter - top;

        int ms = animate ? CardSlideMs : 0;
        Animate(Card, Canvas.TopProperty, top, ms);
        Animate(Card, HeightProperty, height, ms);
        Animate(CardBackground, CardShape.BeakCenterProperty, beakCenter, ms);
        // The canvas's own size never changes, so it would keep arranging the card in its previous slot (and clip it).
        Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Root.InvalidateArrange();
    }

    private void Animate(IAnimatable target, DependencyProperty property, double to, int milliseconds, double? from = null,
        IEasingFunction? ease = null)
    {
        DependencyObject dependency = (DependencyObject)target;
        if (!_expanded && !PreviewMode || !AnimationsEnabled || milliseconds <= 0)
        {
            target.BeginAnimation(property, null);
            dependency.SetValue(property, to);
            return;
        }
        if (from is null && target is Animatable animatable
            && animatable.GetAnimationBaseValue(property) is double baseValue
            && animatable.GetValue(property) is double current
            && Math.Abs(baseValue - to) < 0.001 && Math.Abs(current - to) < 0.001)
            return;

        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease ?? EaseOut };
        if (from is double start) animation.From = start;
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static IEasingFunction CreateEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    // ───────────────────────────── Window plumbing ─────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: no taskbar button, no Alt+Tab entry. No-activate: never steals focus.
        AddExtendedStyle(hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, WS_EX_APPWINDOW);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        if (!PreviewMode)
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            PositionOnPrimaryScreen();
            SetClickThrough(true);
            _tickClock.Start();
            _pointerWatch.Start();
        }

        if (_mode == PanelMode.Pinned) Expand();
    }

    /// Resolution, DPI, monitor or taskbar changes: fit the panel to the new work area.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Left unhandled: DefWindowProc still has to release the raw input.
        if (msg == WM_INPUT && _wakeOnInput) ResumePointerWatch();
        if (!PreviewMode && (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED
                             || (msg == WM_SETTINGCHANGE && wParam.ToInt64() == SPI_SETWORKAREA)))
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PositionOnPrimaryScreen));
        return IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PositionOnPrimaryScreen));

    private void SetClickThrough(bool enabled)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (enabled) AddExtendedStyle(hwnd, WS_EX_TRANSPARENT);
        else AddExtendedStyle(hwnd, 0, WS_EX_TRANSPARENT);
    }

    /// Right edge of the primary monitor's work area, full work-area height, in physical pixels. The scale follows
    /// the work area in DIPs (resolution and DPI together).
    private void PositionOnPrimaryScreen()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            double dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
                ? dpiX / 96.0
                : VisualTreeHelper.GetDpi(this).DpiScaleX;

            RECT work = info.rcWork;
            int height = work.Bottom - work.Top;
            LayOut(height / dpi);
            int width = (int)Math.Round(WindowWidthDip * _scale * dpi);
            int x = work.Right - width;
#if DEBUG
            if (Offscreen) x += 20_000;
#endif

            SetWindowPos(hwnd, HWND_TOPMOST, x, work.Top, width, height, SWP_NOACTIVATE);

            // Cached for the pointer poll's fast path; re-cached on every reposition (display or DPI change).
            _hasWindowRect = true;
            Log.Trace("Edge", $"positioned at {x},{work.Top} {width}x{height} (dpi {dpi}, scale {_scale})");
        }
        catch (Exception ex)
        {
            Log.Error("Position", ex);
        }
    }

    internal void ReassertTopmost()
    {
        if (PreviewMode) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ───────────────────────────── Snapshot support ─────────────────────────────

    internal void ExpandNow() => Expand();

    internal void CollapseNow() => Collapse();

    internal void ShowCardNow(int index)
    {
        Expand();
        for (int i = 0; i < _rings.Length; i++) ScaleRing(i, 1.0);
        ScaleRing(index, RingHoverScale);
        ShowCard(index);
    }

    /// Renders the window at 2× over a wallpaper-like backdrop (teal top, blue middle, warm bottom), or with nothing
    /// behind it (transparent: the README showcase composes it over its own background).
    internal void SaveSnapshot(string path, bool transparent = false)
    {
        const double scale = 2;
        double width = WindowWidthDip * _scale, height = _windowHeightDip;
        // The window is never shown here, so its layout queue never runs: lay out the rendered tree directly.
        Root.InvalidateArrange();
        Host.InvalidateArrange();
        Host.Measure(new Size(width, height));
        Host.Arrange(new Rect(0, 0, width, height));
        UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (DrawingContext dc = backdrop.RenderOpen())
        {
            var gradient = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x7C, 0xC8, 0xD8), 0),
                new(Color.FromRgb(0x2F, 0x86, 0xA8), 0.45),
                new(Color.FromRgb(0x1D, 0x4E, 0x72), 0.75),
                new(Color.FromRgb(0xC9, 0x6A, 0x3B), 1),
            }, 90);
            dc.DrawRectangle(gradient, null, new Rect(0, 0, width, height));
        }
        if (!transparent) bitmap.Render(backdrop);
        bitmap.Render(Host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
