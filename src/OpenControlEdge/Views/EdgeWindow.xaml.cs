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

    // Geometry in design units — the original design scaled to 85 %. RootScale maps them to DIPs. "Along" runs with the
    // screen edge the panel is on, "across" away from it; PanelWidth is the panel's thickness across.
    private const double PanelWidth = 94;
    private const double StripWidth = 5;
    private const double StripHeight = 400;
    private const double MinCardBody = 255;       // the card grows with its header (long account tags), up to MaxCardBody
    private const double MaxCardBody = 340;
    private const double MaxCardHeight = 420;     // on the top and bottom edges: taller cards are cut
    private const double BeakLength = 16;
    private const double CardGap = 6;
    private const double CardSideRoom = 12;
    private const double CardVerticalRoom = 16;  // keep the card inside the window

    /// The window's size across the edge (design units): room for the panel, the gap, the beak and the widest (left or
    /// right edge) or tallest (top or bottom edge) card.
    private double Thickness => _placement.Horizontal
        ? PanelWidth + CardGap + BeakLength + MaxCardHeight + CardSideRoom
        : CardSideRoom + MaxCardBody + BeakLength + CardGap + PanelWidth;

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
    private double _workWidthDip = 1920;
    private double _workHeightDip = ReferenceWorkHeight;
    private double _windowWidthDip;
    private double _windowHeightDip = ReferenceWorkHeight;
    private double _edgeLength = ReferenceWorkHeight;  // the edge's length, design units
    private PanelPlacement _placement = PanelPlacement.Default;
    private double _cardBody = MinCardBody;
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
    private double _panelStart;  // where the panel starts along the edge (design units)
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
    private readonly Dictionary<AiProviderId, (string[] Labels, int Selected, string Culture)> _accounts = new();

    /// Up to this many account tags fit next to a card's title; more go to a row of their own under it.
    private const int MaxTagsBesideTitle = 3;
    private AiProviderId? _infoFor;

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

    /// An account tag of a card was chosen (0 = the default account). The owner persists it and repaints the ring.
    internal event Action<AiProviderId, int>? AccountSelected;

    /// The (i) of a card was opened: the owner answers with ShowSubscription (at once, or after asking the provider).
    internal event Action<AiProviderId>? SubscriptionRequested;

    /// "Abrir Codex" and the like: the owner opens the agent's app or web page.
    internal event Action<AiProviderId>? OpenRequested;

    /// The Modo juego ring was clicked: switch it (or open its settings when it is not enabled).
    internal event Action? GameModeClicked;

    /// The panel was dragged to a new place (manual placement): the owner saves it.
    internal event Action<PanelPlacement>? PlacementDragged;

    /// The panel was resized with its grip (editing the widget): the owner saves the new fixed scale.
    internal event Action<double>? ScaleDragged;

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
        SyncCloseButton();
        SyncOpenButtons();
        SyncUsageTabs();
        // A click anywhere on the card (but on the (i) itself) closes the subscription bubble.
        Card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_infoFor is not null && !IsInfoButton(e.OriginalSource as DependencyObject) && !IsAccountTag(e.OriginalSource as DependencyObject))
                CloseInfo();
        };
        Card.Height = 0;
        ApplyOrientation();
        LayOut(_workWidthDip, ReferenceWorkHeight);

        _holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoldToMove };
        _holdTimer.Tick += OnHoldElapsed;
        _handleRest = new DispatcherTimer(DispatcherPriority.Background) { Interval = HandleRest };
        _handleRest.Tick += OnHandleRested;

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
        _holdTimer.Stop();
        _handleRest.Stop();
        CompositionTarget.Rendering -= OnResizeFrame;
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
            // The chevron keeps pointing right while the panel slides out; it is refreshed on the next expand. While the
            // widget is being edited it stays out until the pointer leaves it afterwards.
            if (!_editing) Collapse();
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

    /// A pin: filled with the text colour while the panel is pinned (white on the dark theme), plain while it hides.
    private void SyncModeButton()
    {
        bool pinned = _mode == PanelMode.Pinned;
        ModeButton.Tag = pinned ? "on" : null;
        ModeIcon.SetResourceReference(IconView.ForegroundProperty, pinned ? ThemeManager.Background : ThemeManager.Text);
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
        WidgetView next = RingKeys.Next(_view, _views.CustomViewEnabled);
        Log.Trace("Edge", $"view button clicked -> {next}");
        ViewChangeRequested?.Invoke(next);
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

    /// The line under a card whose reading is Orb's: "Dato de Orb · Personal · 5 h". Hidden for OCE's own readings.
    private static void SetSource(TextBlock text, string? source)
    {
        text.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        text.Text = source is null ? string.Empty
            : source.Length == 0 ? Loc.Get("Orb.Source") : Loc.Format("Orb.SourceOf", source);
    }

    /// The view and the layouts of the settings. A different view (or a different order or set of rings in it) fades
    /// the rings out and the new ones in when animate and the panel is on screen; otherwise it changes in one step.
    internal void ApplyViews(Settings settings, bool animate)
    {
        _views = settings;
        WidgetView view = settings.ShownView;
        ViewLayout layout = settings.Layout(view);
        bool changed = view != _view || !layout.Equals(_layout);
        _view = view;
        _layout = layout;
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
                double gap = first ? 0 : 10;
                item.Margin = _placement.Horizontal ? new Thickness(gap, 0, 0, 0) : new Thickness(0, gap, 0, 0);
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
        foreach (WidgetView view in RingKeys.Views(_views.CustomViewEnabled))
            max = Math.Max(max, _views.Layout(view).Shown.Count(key => _available[Array.IndexOf(RingKeyOf, key)]));
        if (max == _maxRings) return;
        _maxRings = max;
        if (PreviewMode || !_hasWindowRect) LayOut(_workWidthDip, _workHeightDip);
        else PositionOnScreen();
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
        // While it is the drag handle a keyboard or UI Automation "click" must not quit.
        if (HandleShown) return;
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
        SyncCloseButton();
        SyncOpenButtons();
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

    /// The account tags of a card: one per account (labels[0] is the default one: its name, or its plan), shown instead of
    /// the plan badge when there is more than one. Rebuilt only when the labels or the selection change.
    internal void SetAccounts(AiProviderId provider, IReadOnlyList<string> labels, int selected)
    {
        (Border? plan, StackPanel? beside, WrapPanel? row) = provider switch
        {
            AiProviderId.Claude => (ClaudePlan, ClaudeAccountBadges, ClaudeAccountRow),
            AiProviderId.Codex => (CodexPlan, CodexAccountBadges, CodexAccountRow),
            AiProviderId.Cursor => (CursorPlan, CursorAccountBadges, CursorAccountRow),
            _ => ((Border?)null, (StackPanel?)null, (WrapPanel?)null),
        };
        if (plan is null || beside is null || row is null) return;
        string culture = Loc.Culture.Name;
        if (_accounts.TryGetValue(provider, out var shown) && shown.Selected == selected && shown.Culture == culture
            && shown.Labels.SequenceEqual(labels)) return;
        _accounts[provider] = ([.. labels], selected, culture);
        _syncingAccounts = true;
        beside.Children.Clear();
        row.Children.Clear();
        Panel badges = labels.Count > MaxTagsBesideTitle ? row : beside;
        if (labels.Count > 1)
        {
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var tag = new RadioButton
                {
                    Style = (Style)FindResource("AccountBadge"), Content = labels[i], GroupName = provider + "Account",
                    IsChecked = i == selected, Margin = badges == row ? new Thickness(4, 0, 0, 4) : new Thickness(i == 0 ? 8 : 4, 0, 0, 0),
                    ToolTip = Loc.Format(i == selected ? "Account.Shown" : "Account.Switch", labels[i]),
                };
                AutomationProperties.SetAutomationId(tag, $"{provider}Account{i}");
                AutomationProperties.SetName(tag, labels[i]);
                tag.Checked += (_, _) =>
                {
                    if (_syncingAccounts) return;
                    Log.Trace("Edge", $"{provider} account -> {index}");
                    AccountSelected?.Invoke(provider, index);
                    if (_infoFor == provider) SubscriptionRequested?.Invoke(provider);
                };
                badges.Children.Add(tag);
            }
        }
        beside.Visibility = badges == beside && labels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        row.Visibility = badges == row ? Visibility.Visible : Visibility.Collapsed;
        if (labels.Count > 1) plan.Visibility = Visibility.Collapsed;
        else
        {
            // Back to one account: its plan badge again, from the reading on screen.
            (TextBlock planText, string? planId) = provider switch
            {
                AiProviderId.Claude => (ClaudePlanText, _claude?.Plan),
                AiProviderId.Codex => (CodexPlanText, _codex?.Plan),
                _ => (CursorPlanText, _cursor?.Plan),
            };
            SetPlan(plan, planText, planId);
        }
        _syncingAccounts = false;
        if (_cardVisible) PlaceCard(animate: true);
    }

    private bool HasAccountTags(AiProviderId provider) => _accounts.TryGetValue(provider, out var shown) && shown.Labels.Length > 1;

    // ───────────────────────────── (i) and "Abrir" ─────────────────────────────

    private (Button Info, Button Open, AiProviderId Provider)[]? _cardButtons;

    private (Button Info, Button Open, AiProviderId Provider)[] CardButtons => _cardButtons ??=
    [
        (ClaudeInfo, ClaudeOpen, AiProviderId.Claude), (CodexInfo, CodexOpen, AiProviderId.Codex),
        (CursorInfo, CursorOpen, AiProviderId.Cursor), (OpenCodeInfo, OpenCodeOpen, AiProviderId.OpenCode),
        (DeepSeekInfo, DeepSeekOpen, AiProviderId.DeepSeek), (OpenRouterInfo, OpenRouterOpen, AiProviderId.OpenRouter),
    ];

    /// "Abrir Codex"… and the (i)'s tooltip, in the interface language.
    private void SyncOpenButtons()
    {
        foreach ((Button info, Button open, AiProviderId provider) in CardButtons)
        {
            string tip = Loc.Format("Card.Open", Loc.Get("RingName." + AiProviderSettings.Key(provider)));
            open.ToolTip = tip;
            AutomationProperties.SetName(open, tip);
            info.SetResourceReference(ToolTipProperty, "Tip.Info");
            info.SetResourceReference(AutomationProperties.NameProperty, "Tip.Info");
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        foreach ((_, Button open, AiProviderId provider) in CardButtons)
        {
            if (!ReferenceEquals(open, sender)) continue;
            Log.Trace("Edge", $"open {provider}");
            OpenRequested?.Invoke(provider);
        }
    }

    private void OnInfoClick(object sender, RoutedEventArgs e)
    {
        foreach ((Button info, _, AiProviderId provider) in CardButtons)
        {
            if (!ReferenceEquals(info, sender)) continue;
            if (_infoFor == provider) { CloseInfo(); return; }
            CloseInfo();
            _infoFor = provider;
            info.Tag = "on";
            InfoTitle.Text = Loc.Get("Sub.Title");
            InfoText.Text = Loc.Get("Sub.Loading");
            InfoBubble.Visibility = Visibility.Visible;
            InvalidateUpTo(InfoText, CardContent);
            if (_cardVisible) PlaceCard(animate: true);
            SubscriptionRequested?.Invoke(provider);
            return;
        }
    }

    /// The owner's answer to SubscriptionRequested; ignored once the bubble is closed or shows another agent.
    internal void ShowSubscription(AiProviderId provider, string text)
    {
        if (_infoFor != provider) return;
        InfoText.Text = text;
        InvalidateUpTo(InfoText, CardContent);
        if (_cardVisible) PlaceCard(animate: true);
    }

    private static void InvalidateTree(DependencyObject element)
    {
        if (element is UIElement ui) ui.InvalidateMeasure();
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(element); i < n; i++) InvalidateTree(VisualTreeHelper.GetChild(element, i));
    }

    /// A new text in the card only invalidates its own TextBlock until the next layout pass, and PlaceCard measures the
    /// card itself: every level up to it is invalidated so that measure is not served from the cache.
    private static void InvalidateUpTo(UIElement from, UIElement to)
    {
        for (DependencyObject? element = from; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement ui) ui.InvalidateMeasure();
            if (ReferenceEquals(element, to)) break;
        }
    }

    /// place: re-fit the card to its content now (not when the card is about to be placed or hidden anyway).
    private void CloseInfo(bool place = true)
    {
        if (_infoFor is null) return;
        _infoFor = null;
        foreach ((Button info, _, _) in CardButtons) info.Tag = null;
        InfoBubble.Visibility = Visibility.Collapsed;
        if (place && _cardVisible) PlaceCard(animate: true);
    }

    private static bool IsAccountTag(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is RadioButton { GroupName: string group } && group.EndsWith("Account", StringComparison.Ordinal)) return true;
        return false;
    }

    private bool IsInfoButton(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is Button button && CardButtons.Any(b => ReferenceEquals(b.Info, button))) return true;
        return false;
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
        GameModeLabel.Text = state.Busy ? "…" : Loc.Get(state.Active ? "Game.On" : "Game.Off");
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

    /// Places the panel along its edge: centred, or with its centre where the user dragged it (manual placement), always
    /// whole inside the edge. Its length changes when a ring is shown or hidden.
    private void CenterPanel(bool animate)
    {
        double length = PanelLength();
        _panelLength = length;
        double center = _placement.Manual ? _placement.Offset * _edgeLength : _edgeLength / 2;
        _panelStart = Math.Round(Math.Clamp(center - length / 2, 0, Math.Max(0, _edgeLength - length)));
        Animate(EdgePanel, _placement.Horizontal ? Canvas.LeftProperty : Canvas.TopProperty, _panelStart, animate ? PanelMs : 0);
        // The strip (auto mode, collapsed) sits where the panel is.
        double strip = Math.Min(StripHeight, _edgeLength);
        double stripStart = Math.Clamp(_panelStart + length / 2 - strip / 2, 0, Math.Max(0, _edgeLength - strip));
        Canvas.SetLeft(Strip, _placement.Horizontal ? stripStart : _placement.Edge == PanelEdge.Left ? 0 : Thickness - Strip.Width);
        Canvas.SetTop(Strip, !_placement.Horizontal ? stripStart : _placement.Edge == PanelEdge.Top ? 0 : Thickness - Strip.Height);
        PlaceGrip();
    }

    /// The panel's length along the edge (design units) as it is now.
    private double PanelLength()
    {
        // Invalidate every level explicitly: a bare Measure() on an ancestor returns a cached size when only a
        // descendant changed.
        PanelStack.InvalidateMeasure();
        PanelRoot.InvalidateMeasure();
        EdgePanel.InvalidateMeasure();
        if (_placement.Horizontal)
        {
            EdgePanel.Measure(new Size(double.PositiveInfinity, PanelWidth));
            return EdgePanel.DesiredSize.Width;
        }
        EdgePanel.Measure(new Size(PanelWidth, double.PositiveInfinity));
        return EdgePanel.DesiredSize.Height;
    }

    // ───────────────────────────── Scale ─────────────────────────────

    /// "uiScale" from the settings: a fixed scale, or null for automatic. Takes effect immediately.
    internal void ApplyScale(double? uiScale)
    {
        double? scale = uiScale is double s ? Math.Clamp(s, SettingsStore.MinUiScale, SettingsStore.MaxUiScale) : null;
        if (scale == _uiScale && _hasWindowRect && !PreviewMode) return;
        _uiScale = scale;
        if (PreviewMode || !_hasWindowRect) LayOut(_workWidthDip, _workHeightDip);
        else PositionOnScreen();
    }

    /// Snapshot support: lays the window out as if the monitor's work area were this many DIPs tall (and wide: 16:9
    /// by default).
    internal void PreviewWorkArea(double workHeightDip, double? workWidthDip = null) =>
        LayOut(workWidthDip ?? Math.Round(workHeightDip * 1920 / ReferenceWorkHeight), workHeightDip);

    internal double Scale => _scale;

    /// Picks the scale for a work area of the given size (DIPs) and lays the canvas out in design units. The window runs
    /// the whole length of its edge and is Thickness deep.
    /// Automatic: proportional to the work area's height, limited to MinAutoScale–MaxAutoScale so the vectors stay crisp.
    /// Either way it never exceeds what fits the panel with the rings of its largest view along its edge, so nothing
    /// is ever cut off.
    private void LayOut(double workWidthDip, double workHeightDip)
    {
        _workWidthDip = workWidthDip;
        _workHeightDip = workHeightDip;
        bool horizontal = _placement.Horizontal;
        double edgeLength = horizontal ? workWidthDip : workHeightDip;
        double preferred = _uiScale ?? Math.Clamp(workHeightDip / ReferenceWorkHeight, MinAutoScale, MaxAutoScale);
        double fit = (edgeLength - 2 * ScreenMargin) / FullPanelHeight();
        // Across the edge the window must fit the work area too (a top or bottom panel on a short screen).
        double across = (horizontal ? workHeightDip : workWidthDip) / Thickness;
        _scale = Math.Round(Math.Max(MinFitScale, Math.Min(Math.Min(preferred, fit), Math.Max(MinFitScale, across))), 3);
        _edgeLength = edgeLength / _scale;
        _windowWidthDip = horizontal ? workWidthDip : Thickness * _scale;
        _windowHeightDip = horizontal ? Thickness * _scale : workHeightDip;

        RootScale.ScaleX = RootScale.ScaleY = _scale;
        Root.Width = horizontal ? _edgeLength : Thickness;
        Root.Height = horizontal ? Thickness : _edgeLength;
        // While the grip is dragged the window keeps the room it was given for the largest scale (GrowForResize): the
        // canvas grows and shrinks inside it, against the screen edge, with no window resize on every frame.
        if (!_resizeRoom)
        {
            Width = _windowWidthDip;
            Height = _windowHeightDip;
        }

        Strip.Width = horizontal ? Math.Min(StripHeight, _edgeLength) : StripWidth / _scale;
        Strip.Height = horizontal ? StripWidth / _scale : Math.Min(StripHeight, _edgeLength);
        Canvas.SetLeft(EdgePanel, _placement.Edge switch { PanelEdge.Right => Thickness - PanelWidth, PanelEdge.Left => 0, _ => Canvas.GetLeft(EdgePanel) is double l && !double.IsNaN(l) ? l : 0 });
        Canvas.SetTop(EdgePanel, _placement.Edge switch { PanelEdge.Bottom => Thickness - PanelWidth, PanelEdge.Top => 0, _ => Canvas.GetTop(EdgePanel) is double t && !double.IsNaN(t) ? t : 0 });

        LayOutButtons();
        CenterPanel(animate: false);
        if (_cardVisible) PlaceCard(animate: false);
        Log.Trace("Edge", $"scale {_scale} (preferred {preferred:0.###}, fit {fit:0.###}) for {workWidthDip:0}x{workHeightDip:0} DIP, {_placement.Edge}");
    }

    /// Length of the panel along its edge (design units) with as many rings as the largest view shows (UpdateMaxRings),
    /// whatever is visible right now, so the scale does not jump when switching views.
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
        double length = PanelLength();
        for (int i = 0; i < _ringItems.Length; i++) _ringItems[i].Visibility = saved[i];
        return length;
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
        ResizeGrip.Width = ResizeGrip.Height = ButtonDiameter;
        GripIcon.Width = GripIcon.Height = Math.Round(ButtonDiameter * 0.5);
    }

    // ───────────────────────────── Placement ─────────────────────────────

    internal PanelPlacement Placement => _placement;

    /// Edge, monitor and place along the edge (Ajustes › Personalización, or the drag handle). The panel moves at once.
    internal void ApplyPlacement(PanelPlacement placement)
    {
        if (placement == _placement) return;
        bool turned = placement.Edge != _placement.Edge;
        bool moved = placement.Monitor != _placement.Monitor;
        _placement = placement;
        Log.Trace("Edge", $"placement -> {placement}");
        if (turned)
        {
            HideCard();
            ApplyOrientation();
        }
        if (turned || moved)
        {
            if (PreviewMode || !_hasWindowRect) LayOut(_workWidthDip, _workHeightDip);
            else PositionOnScreen();
        }
        else CenterPanel(animate: false);
        SyncCloseButton();
    }

    /// What depends on the edge: the panel's shape and the direction of its rings, the card's beak and margins, and
    /// where the collapsed panel hides.
    private void ApplyOrientation()
    {
        PanelEdge edge = _placement.Edge;
        bool horizontal = _placement.Horizontal;
        PanelShape.Edge = edge;
        CardBackground.Side = edge;
        // Against the screen edge: the same as the window's top left corner, except while resizing (see LayOut).
        Root.HorizontalAlignment = edge == PanelEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Root.VerticalAlignment = edge == PanelEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        ResizeGrip.Cursor = horizontal ? System.Windows.Input.Cursors.SizeWE : System.Windows.Input.Cursors.SizeNS;
        EdgePanel.Width = horizontal ? double.NaN : PanelWidth;
        EdgePanel.Height = horizontal ? PanelWidth : double.NaN;
        PanelRoot.Margin = horizontal ? new Thickness(38, 0, 38, 0) : new Thickness(0, 38, 0, 38);
        PanelStack.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        PanelStack.Margin = horizontal ? new Thickness(20, 0, 14, 0) : new Thickness(0, 20, 0, 14);
        PanelStack.VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        RingStack.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        PanelButtons.Margin = horizontal ? new Thickness(14, 0, 0, 0) : new Thickness(0, 14, 0, 0);
        PanelButtons.VerticalAlignment = VerticalAlignment.Center;
        foreach (FrameworkElement item in _ringItems) item.VerticalAlignment = VerticalAlignment.Center;
        ContentClip.Margin = edge switch
        {
            PanelEdge.Left => new Thickness(BeakLength, 0, 0, 0),
            PanelEdge.Top => new Thickness(0, BeakLength, 0, 0),
            PanelEdge.Bottom => new Thickness(0, 0, 0, BeakLength),
            _ => new Thickness(0, 0, BeakLength, 0),
        };
        // Animations of the other axis would keep holding their last value over the new places.
        foreach (DependencyProperty property in new[] { Canvas.LeftProperty, Canvas.TopProperty }) { EdgePanel.BeginAnimation(property, null); Card.BeginAnimation(property, null); }
        // Collapsed, the panel hides past its edge.
        PanelShift.BeginAnimation(TranslateTransform.XProperty, null);
        PanelShift.BeginAnimation(TranslateTransform.YProperty, null);
        (PanelShift.X, PanelShift.Y) = _expanded ? (0, 0) : HiddenShift();
        ApplyRingVisibility();
    }

    /// The panel's offset while collapsed: one panel thickness out past its edge.
    private (double X, double Y) HiddenShift() => _placement.Edge switch
    {
        PanelEdge.Left => (-PanelWidth, 0),
        PanelEdge.Top => (0, -PanelWidth),
        PanelEdge.Bottom => (0, PanelWidth),
        _ => (PanelWidth, 0),
    };

    // ✕ is the handle that drags the panel (a four-way arrow) for a while after a long press on it, and the whole time
    // the widget is being edited from Ajustes; then it is ✕ again.
    private static readonly TimeSpan HoldToMove = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan HandleRest = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _handleRest;
    private bool _moveArmed;
    private bool _editing;

    private bool HandleShown => _moveArmed || _editing;

    private void SyncCloseButton()
    {
        bool handle = HandleShown;
        CloseIcon.Icon = handle ? Icons.Move : Icons.Close;
        CloseButton.Cursor = handle ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Hand;
        string tip = handle ? "Tip.Drag" : "Tip.Close";
        CloseButton.SetResourceReference(ToolTipProperty, tip);
        CloseButton.SetResourceReference(AutomationProperties.NameProperty, tip);
    }

    /// Held long enough: ✕ turns into the handle under the finger and the drag starts at once. A release before this is
    /// an ordinary click (quit).
    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (_dragging || !CloseButton.IsMouseCaptured || System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        Log.Trace("Edge", "close button held: drag handle");
        _moveArmed = true;
        SyncCloseButton();
        Animate(CloseScale, ScaleTransform.ScaleXProperty, 1, 220, from: 1.2);
        Animate(CloseScale, ScaleTransform.ScaleYProperty, 1, 220, from: 1.2);
        // The button lets go of the pointer (so its release is no click) and the drag takes it over.
        CloseButton.ReleaseMouseCapture();
        StartDrag();
    }

    /// Three seconds after the panel was left in its place, the handle is ✕ again (not while editing the widget).
    private void RestHandleSoon()
    {
        _handleRest.Stop();
        if (_moveArmed && !_editing) _handleRest.Start();
    }

    private void OnHandleRested(object? sender, EventArgs e)
    {
        _handleRest.Stop();
        if (_dragging || !_moveArmed) return;
        _moveArmed = false;
        SyncCloseButton();
    }

    // ───────────────────────────── Editing the widget ─────────────────────────────

    internal bool Editing => _editing;

    /// The fixed scale applied now (Ajustes or the grip), or null for automatic.
    internal double? UiScale => _uiScale;

    /// Ajustes › Personalización › Editar widget: the panel comes out and stays out, ✕ is the drag handle and a grip
    /// next to the panel resizes it, until EndEdit.
    internal void BeginEdit()
    {
        if (_editing) return;
        Log.Trace("Edge", "edit widget: on");
        _editing = true;
        _moveArmed = false;
        _handleRest.Stop();
        HideCard();
        Expand();
        SyncCloseButton();
        ResizeGrip.Visibility = Visibility.Visible;
        PlaceGrip();
        Animate(ResizeGrip, OpacityProperty, 1, 180);
        ResumePointerWatch();
    }

    /// Editing is over (the button again, or Ajustes closed): ✕ is back, the grip goes. Place and size were already
    /// saved by each drag; the owner saves them once more.
    internal void EndEdit()
    {
        if (!_editing) return;
        Log.Trace("Edge", "edit widget: off");
        if (_resizing) ResizeGrip.ReleaseMouseCapture();
        if (_dragging) CloseButton.ReleaseMouseCapture();
        _editing = false;
        _moveArmed = false;
        _handleRest.Stop();
        SyncCloseButton();
        ResizeGrip.BeginAnimation(OpacityProperty, null);
        ResizeGrip.Opacity = 0;
        ResizeGrip.Visibility = Visibility.Collapsed;
        // Auto mode: the panel slides away on its own once the pointer is off it.
        _outside.Reset();
        ResumePointerWatch();
    }

    // The grip sits just past the panel's end along the edge (before its start when the panel reaches the end of the
    // edge), centred across the panel's thickness.
    private const double GripGap = 6;
    private double _panelLength;

    private void PlaceGrip()
    {
        if (ResizeGrip.Visibility != Visibility.Visible) return;
        double size = ButtonDiameter;
        double after = _panelStart + _panelLength + GripGap;
        double along = after + size <= _edgeLength ? after : Math.Max(0, _panelStart - GripGap - size);
        double across = _placement.Edge is PanelEdge.Right or PanelEdge.Bottom ? Thickness - PanelWidth / 2 - size / 2 : PanelWidth / 2 - size / 2;
        Canvas.SetLeft(ResizeGrip, _placement.Horizontal ? along : across);
        Canvas.SetTop(ResizeGrip, _placement.Horizontal ? across : along);
    }

    // Resizing: the grip's distance from the panel's centre along the edge sets the scale (twice as far, twice as big).
    // The pointer only sets a target; every frame the scale glides a third of the way to it, so the size flows.
    private bool _resizing;
    private bool _resizeRoom;
    private double _resizeFrom;     // the scale when the grip was grabbed
    private double _resizeReach;    // pointer minus the panel's centre along the edge then (physical px)
    private double _resizeCentre;   // the panel's centre along the edge (physical px, screen coordinates)
    private double _resizeTarget;
    private bool _resizeFrames;
    private const double ResizeGlide = 0.3;

    private void OnGripDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_editing || PreviewMode || _resizing || !GetCursorPos(out POINT cursor)) return;
        if (Monitors.Find(_placement.Monitor) is not MonitorInfo monitor) return;
        bool horizontal = _placement.Horizontal;
        double pixels = _scale * monitor.Dpi / 96.0;
        _resizeCentre = (horizontal ? monitor.Work.Left : monitor.Work.Top) + (_panelStart + _panelLength / 2) * pixels;
        _resizeReach = (horizontal ? cursor.X : cursor.Y) - _resizeCentre;
        if (Math.Abs(_resizeReach) < 8) return;
        _resizeFrom = _resizeTarget = _uiScale ?? _scale;
        _resizing = ResizeGrip.CaptureMouse();
        if (!_resizing) return;
        HideCard();
        _uiScale = _resizeFrom;
        GrowForResize(monitor);
        if (!_resizeFrames) { _resizeFrames = true; CompositionTarget.Rendering += OnResizeFrame; }
        Log.Trace("Edge", $"resize start at scale {_resizeFrom:0.###}");
    }

    private void OnGripMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_resizing || !GetCursorPos(out POINT cursor)) return;
        double reach = (_placement.Horizontal ? cursor.X : cursor.Y) - _resizeCentre;
        double ratio = Math.Max(0, reach / _resizeReach);
        _resizeTarget = Math.Clamp(_resizeFrom * ratio, SettingsStore.MinUiScale, SettingsStore.MaxUiScale);
    }

    private void OnGripUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        e.Handled = true;
        ResizeGrip.ReleaseMouseCapture();
    }

    // The frames carry on until the scale has reached the last target, then FinishResize.
    private void OnGripLost(object sender, System.Windows.Input.MouseEventArgs e) => _resizing = false;

    private void OnResizeFrame(object? sender, EventArgs e)
    {
        double current = _uiScale ?? _scale;
        double next = current + (_resizeTarget - current) * ResizeGlide;
        if (Math.Abs(_resizeTarget - next) < 0.002) next = _resizeTarget;
        if (Math.Abs(next - current) > 0.0001)
        {
            _uiScale = next;
            LayOut(_workWidthDip, _workHeightDip);
        }
        if (!_resizing && next == _resizeTarget) FinishResize();
    }

    private void FinishResize()
    {
        CompositionTarget.Rendering -= OnResizeFrame;
        _resizeFrames = false;
        _resizeRoom = false;
        double scale = Math.Round(_uiScale ?? _scale, 2);
        _uiScale = scale;
        PositionOnScreen();
        Log.Trace("Edge", $"resize end at scale {scale:0.##}");
        ScaleDragged?.Invoke(scale);
    }

    /// Before the grip moves: the window takes the room of the largest scale across the edge (it already runs the whole
    /// length of it), so each frame only rescales the canvas inside it.
    private void GrowForResize(MonitorInfo monitor)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        double dpi = monitor.Dpi / 96.0;
        RECT work = monitor.Work;
        int workWidth = work.Right - work.Left, workHeight = work.Bottom - work.Top;
        bool horizontal = _placement.Horizontal;
        int room = (int)Math.Min(horizontal ? workHeight : workWidth, Math.Round(Thickness * SettingsStore.MaxUiScale * dpi));
        int width = horizontal ? workWidth : room;
        int height = horizontal ? room : workHeight;
        int x = _placement.Edge == PanelEdge.Right ? work.Right - width : work.Left;
        int y = _placement.Edge == PanelEdge.Bottom ? work.Bottom - height : work.Top;
        // Only the HWND: setting Width first would grow the window away from the edge for a frame before it moves back.
        _resizeRoom = true;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
    }

    // The drag: the panel follows the pointer along the nearest edge of the monitor under it.
    private bool _dragging;
    private double _dragGrab;          // pointer minus the panel's centre along the edge at the start (design units)
    private IReadOnlyList<MonitorInfo> _dragMonitors = [];
    private bool _dragQueued;
    private PanelPlacement _dragStart = PanelPlacement.Default;
    private const int EdgeHysteresis = 32;  // physical px nearer another edge before the panel jumps to it

    private void OnHandleDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PreviewMode || _resizing) return;
        if (!HandleShown)
        {
            // ✕ as usual: a click quits; held down, it becomes the handle (OnHoldElapsed).
            _holdTimer.Stop();
            _holdTimer.Start();
            return;
        }
        e.Handled = true;  // no Click: while it is the handle this button never closes the app
        StartDrag();
    }

    private void StartDrag()
    {
        _handleRest.Stop();
        HideCard();
        _dragMonitors = Monitors.All();
        _dragStart = _placement;
        _dragging = CloseButton.CaptureMouse();
        Point local = System.Windows.Input.Mouse.GetPosition(Root);
        _dragGrab = (_placement.Horizontal ? local.X : local.Y) - (_panelStart + PanelLength() / 2);
        Log.Trace("Edge", "drag start");
    }

    private void OnHandleMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Every move relays the whole layered window out: one per frame is enough.
        if (!_dragging || _dragQueued) return;
        _dragQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { _dragQueued = false; DragTo(); }));
    }

    private void DragTo()
    {
        if (!_dragging || !GetCursorPos(out POINT cursor)) return;
        MonitorInfo? monitor = _dragMonitors.FirstOrDefault(m => cursor.X >= m.Bounds.Left && cursor.X < m.Bounds.Right
                                                                  && cursor.Y >= m.Bounds.Top && cursor.Y < m.Bounds.Bottom)
                               ?? Monitors.At(cursor.X, cursor.Y);
        if (monitor is null) return;
        RECT work = monitor.Work;
        int left = cursor.X - work.Left, right = work.Right - cursor.X, top = cursor.Y - work.Top, bottom = work.Bottom - cursor.Y;
        int Distance(PanelEdge edge) => edge switch { PanelEdge.Left => left, PanelEdge.Top => top, PanelEdge.Bottom => bottom, _ => right };
        PanelEdge nearest = new[] { PanelEdge.Right, PanelEdge.Left, PanelEdge.Top, PanelEdge.Bottom }.MinBy(Distance);
        string? current = _placement.Monitor ?? _dragMonitors.FirstOrDefault(m => m.Primary)?.Device;
        bool sameMonitor = monitor.Device.Equals(current, StringComparison.OrdinalIgnoreCase);
        PanelEdge edge = sameMonitor && Distance(_placement.Edge) <= Distance(nearest) + EdgeHysteresis ? _placement.Edge : nearest;
        bool horizontal = edge is PanelEdge.Top or PanelEdge.Bottom;
        double along = horizontal ? (cursor.X - work.Left) / (double)Math.Max(1, work.Right - work.Left)
                                  : (cursor.Y - work.Top) / (double)Math.Max(1, work.Bottom - work.Top);
        if (edge == _placement.Edge && sameMonitor)
        {
            // Same edge: the grab point stays under the pointer.
            double lengthPx = (horizontal ? work.Right - work.Left : work.Bottom - work.Top);
            along -= _dragGrab * _scale * (monitor.Dpi / 96.0) / Math.Max(1, lengthPx);
        }
        else _dragGrab = 0;
        ApplyPlacement(_placement with { Edge = edge, Manual = true, Offset = Math.Round(Math.Clamp(along, 0, 1), 4), Monitor = monitor.Device });
    }

    private void OnHandleUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _holdTimer.Stop();
        if (!_dragging) return;
        e.Handled = true;
        CloseButton.ReleaseMouseCapture();
    }

    private void OnHandleLost(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Log.Trace("Edge", $"drag end -> {_placement}");
        if (_placement != _dragStart) PlacementDragged?.Invoke(_placement);
        RestHandleSoon();
    }

    // ───────────────────────────── Data ─────────────────────────────

    internal void SetClaude(ClaudeSnapshot snapshot)
    {
        _claude = snapshot;
        SetRingVisible(RingClaude, !snapshot.Hidden);
        if (snapshot.Hidden) return;
        SetPlan(ClaudePlan, ClaudePlanText, HasAccountTags(AiProviderId.Claude) ? null : snapshot.Plan);
        SetInfoButton(ClaudeInfo, snapshot.Plan);

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

            SetSource(ClaudeSource, snapshot.Source);
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
        SetPlan(CodexPlan, CodexPlanText, HasAccountTags(AiProviderId.Codex) ? null : snapshot.Plan);
        SetInfoButton(CodexInfo, snapshot.Plan);

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

            SetSource(CodexSource, snapshot.Source);
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
        SetPlan(CursorPlan, CursorPlanText, HasAccountTags(AiProviderId.Cursor) ? null : snapshot.Plan);
        SetInfoButton(CursorInfo, snapshot.Plan);

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
            SetSource(CursorSource, snapshot.Source);
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

    /// The (i) is for a subscription: none on a free plan (an unknown plan keeps it; the bubble says what it can).
    private void SetInfoButton(Button info, string? plan)
    {
        bool free = string.Equals(plan, "free", StringComparison.OrdinalIgnoreCase);
        info.Visibility = free ? Visibility.Collapsed : Visibility.Visible;
        if (free && CardButtons.FirstOrDefault(b => ReferenceEquals(b.Info, info)).Provider is var provider && _infoFor == provider) CloseInfo();
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

        if (_dragging || _resizing || !GetCursorPos(out POINT cursor)) return;
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
            if (_mode == PanelMode.Auto && Contains(Strip, screen) && !IsAnyMouseButtonDown())
            {
                if (!_stripDwell.IsRunning) _stripDwell.Restart();
                if (!_edgeNeedsDwell || _stripDwell.Elapsed >= StripDwell) { _stripDwell.Reset(); Expand(); }
            }
            else _stripDwell.Reset();
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
                      || (_editing && Contains(ResizeGrip, screen))
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

        // While the widget is being edited the panel stays out.
        if (_mode == PanelMode.Auto && !_editing)
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
               || (_cardVisible && Contains(Card, new Point(point.X, point.Y)))
               || (_editing && Contains(ResizeGrip, new Point(point.X, point.Y)));
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

        Animate(PanelShift, _placement.Horizontal ? TranslateTransform.YProperty : TranslateTransform.XProperty, 0, PanelMs);
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
        (double hiddenX, double hiddenY) = HiddenShift();
        if (_placement.Horizontal) Animate(PanelShift, TranslateTransform.YProperty, hiddenY, PanelMs, ease: EaseIn);
        else Animate(PanelShift, TranslateTransform.XProperty, hiddenX, PanelMs, ease: EaseIn);
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

        CloseInfo(place: false);
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
            CardShift.BeginAnimation(TranslateTransform.XProperty, null);
            CardShift.BeginAnimation(TranslateTransform.YProperty, null);
            CardShift.X = CardShift.Y = 0;
            double from = _placement.Edge is PanelEdge.Left or PanelEdge.Top ? -10 : 10;
            Animate(CardShift, _placement.Horizontal ? TranslateTransform.YProperty : TranslateTransform.XProperty, 0, 220, from: from);
            Animate(Card, OpacityProperty, 1, 180);
        }
    }

    private void HideCard()
    {
        if (!_cardVisible) return;
        Log.Trace("Edge", "hide card");
        _cardVisible = false;
        CloseInfo();
        Card.IsHitTestVisible = false;
        _timeTextTimer.Stop();
        _activeRing = -1;
        Animate(Card, OpacityProperty, 0, 150);
    }

    /// Centres the card on the active ring (clamped to the window) and points the beak at the ring. The card is as wide
    /// as its header needs (two long account tags), MinCardBody to MaxCardBody.
    private void PlaceCard(bool animate)
    {
        if (_activeRing < 0) return;

        // Measure only the card, never UpdateLayout (that would remeasure the whole layered window on every sample).
        // Always unconstrained: a layout pass measures it within the card's current height (0 before the first show),
        // and Measure itself is a no-op while neither the content nor the constraint changed.
        // A new text or tag only invalidates its own element until the next layout pass: the whole card is invalidated so
        // these measures are not served from the cache (the card is a few dozen elements, and only on screen here).
        InvalidateTree(_cards[_activeRing]);
        InvalidateUpTo(_cards[_activeRing], CardContent);
        double body = MinCardBody;
        if (_cards[_activeRing] is Panel { Children.Count: > 0 } card && card.Children[0] is FrameworkElement header)
        {
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double margins = CardContent.Margin.Left + CardContent.Margin.Right;
            body = Math.Clamp(Math.Ceiling(header.DesiredSize.Width + margins + 2), MinCardBody, MaxCardBody);
        }
        _cardBody = body;
        CardContent.Measure(new Size(body, double.PositiveInfinity));
        double height = Math.Ceiling(CardContent.DesiredSize.Height);
        if (_placement.Horizontal) height = Math.Min(height, MaxCardHeight);

        // Measured against the panel's target place, so the card aims at where the ring ends up while the panel is
        // still re-centring after a ring appeared or disappeared.
        FrameworkElement ring = _rings[_activeRing];
        Point ringPoint = ring.TranslatePoint(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2), EdgePanel);
        double ringCenter = _panelStart + (_placement.Horizontal ? ringPoint.X : ringPoint.Y);
        int ms = animate ? CardSlideMs : 0;

        if (_placement.Horizontal)
        {
            double maxLeft = Math.Max(CardVerticalRoom, _edgeLength - CardVerticalRoom - body);
            double left = Math.Min(Math.Max(ringCenter - body / 2, CardVerticalRoom), maxLeft);
            double cardHeight = height + BeakLength;
            double top = _placement.Edge == PanelEdge.Top ? PanelWidth + CardGap : Thickness - PanelWidth - CardGap - cardHeight;
            Animate(Card, Canvas.LeftProperty, left, ms);
            Animate(Card, Canvas.TopProperty, top, ms);
            Animate(Card, WidthProperty, body, ms);
            Animate(Card, HeightProperty, cardHeight, ms);
            Animate(CardBackground, CardShape.BeakCenterProperty, ringCenter - left, ms);
        }
        else
        {
            double maxTop = Math.Max(CardVerticalRoom, _edgeLength - CardVerticalRoom - height);
            double top = Math.Min(Math.Max(ringCenter - height / 2, CardVerticalRoom), maxTop);
            double cardWidth = body + BeakLength;
            double left = _placement.Edge == PanelEdge.Left ? PanelWidth + CardGap : Thickness - PanelWidth - CardGap - cardWidth;
            Animate(Card, Canvas.LeftProperty, left, ms);
            Animate(Card, Canvas.TopProperty, top, ms);
            Animate(Card, WidthProperty, cardWidth, ms);
            Animate(Card, HeightProperty, height, ms);
            Animate(CardBackground, CardShape.BeakCenterProperty, ringCenter - top, ms);
        }
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
            PositionOnScreen();
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
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PositionOnScreen));
        return IntPtr.Zero;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PositionOnScreen));

    private void SetClickThrough(bool enabled)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (enabled) AddExtendedStyle(hwnd, WS_EX_TRANSPARENT);
        else AddExtendedStyle(hwnd, 0, WS_EX_TRANSPARENT);
    }

    /// Along the chosen edge of the chosen monitor's work area (the primary one when that monitor is gone), the whole
    /// length of the edge, in physical pixels. The scale follows the work area in DIPs (resolution and DPI together).
    /// Pointers cross the strip on its way to the taskbar, a title bar or another monitor: there it waits this long.
    private static readonly TimeSpan StripDwell = TimeSpan.FromMilliseconds(250);
    private readonly Stopwatch _stripDwell = new();
    private bool _edgeNeedsDwell;

    private void PositionOnScreen()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (Monitors.Find(_placement.Monitor) is not MonitorInfo monitor) return;
            double dpi = monitor.Dpi / 96.0;
            RECT work = monitor.Work;
            int workWidth = work.Right - work.Left, workHeight = work.Bottom - work.Top;
            // The work area's edge is not the screen's here (the taskbar sits there), or it is a top or bottom edge.
            _edgeNeedsDwell = _placement.Horizontal || _placement.Edge switch
            {
                PanelEdge.Left => work.Left != monitor.Bounds.Left,
                _ => work.Right != monitor.Bounds.Right,
            };
            LayOut(workWidth / dpi, workHeight / dpi);
            int width = _placement.Horizontal ? workWidth : (int)Math.Round(_windowWidthDip * dpi);
            int height = _placement.Horizontal ? (int)Math.Round(_windowHeightDip * dpi) : workHeight;
            int x = _placement.Edge == PanelEdge.Right ? work.Right - width : work.Left;
            int y = _placement.Edge == PanelEdge.Bottom ? work.Bottom - height : work.Top;
#if DEBUG
            if (Offscreen) x += 20_000;
#endif

            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);

            // Cached for the pointer poll's fast path; re-cached on every reposition (display or DPI change).
            _hasWindowRect = true;
            Log.Trace("Edge", $"positioned at {x},{y} {width}x{height} on {monitor.Device} (dpi {dpi}, scale {_scale})");
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

    /// The (i) bubble of the card on screen, open with this text.
    internal void OpenInfoNow(AiProviderId provider, string text)
    {
        foreach ((Button info, _, AiProviderId id) in CardButtons)
            if (id == provider) OnInfoClick(info, new RoutedEventArgs());
        ShowSubscription(provider, text);
    }

    internal void CollapseNow() => Collapse();

    /// The panel's last button shows ✕ (quit), not the drag handle, and the size grip is gone.
    internal bool CloseIsClose => CloseIcon.Icon == Icons.Close && ResizeGrip.Visibility != Visibility.Visible;

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
        double width = _windowWidthDip, height = _windowHeightDip;
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
