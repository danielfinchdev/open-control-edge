using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
/// The panel ends in three round buttons: pin/hide, settings and close. Provider and sensor rings can come and go.
/// Everything is laid out in design units and scaled as a whole to the monitor's work area (ApplyScale).
/// Hover is driven by polling the cursor position: while collapsed the window is click-through
/// (WS_EX_TRANSPARENT) and receives no mouse input at all, so events could not detect the strip.
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
    private const double MinFitScale = 0.6;       // only reached when all eight rings would not fit otherwise
    private const double ScreenMargin = 4;        // DIPs kept free above and below the panel

    // Round buttons: three in a row when that gives at least MinRowButtonDip, otherwise two above and one below.
    private const double ButtonGap = 5;
    private const double ButtonSidePadding = 8;
    private const double RowButtonDiameter = (PanelWidth - 2 * ButtonSidePadding - 2 * ButtonGap) / 3;
    private const double StackedButtonDiameter = 32;
    private const double StackedButtonGap = 8;
    private const double MinRowButtonDip = 30;

    private double _scale = 1;
    private double _windowHeightDip = ReferenceWorkHeight;
    private double _availableHeight = ReferenceWorkHeight;  // design units
    private double? _uiScale;

    private const int PanelMs = 250;
    private const int RingHoverMs = 150;
    private const int CardSlideMs = 250;
    private const double RingHoverScale = 1.08;
    private static readonly TimeSpan CollapseGrace = TimeSpan.FromMilliseconds(300);

    // Pointer polling: NearPoll only while the cursor is over this window, FarPoll the rest of the time
    // (the overwhelmingly common case), where a tick is a GetCursorPos plus four integer comparisons.
    private static readonly TimeSpan FarPoll = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan NearPoll = TimeSpan.FromMilliseconds(30);

    private static readonly IEasingFunction EaseOut = CreateEaseOut();

    private readonly DateTime _sessionStart;
    private readonly DispatcherTimer _pointerWatch;
    private readonly DispatcherTimer _timeTextTimer;
    private readonly Stopwatch _outside = new();
    private readonly Stopwatch _tickClock = new();
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
    private UsageView _usageView = UsageView.Session;
    private bool _syncingTabs;

    internal bool AnimationsEnabled { get; set; } = true;

    /// Snapshot rendering: no positioning, no topmost juggling, no pointer polling.
    internal bool PreviewMode { get; set; }

    internal event Action<bool>? ExpandedChanged;

    /// The pin/hide button was pressed: pinned → auto or auto → pinned. The owner persists it and calls ApplyMode.
    internal event Action<PanelMode>? ModeChangeRequested;

    /// The "Sesión" / "Total" tab of a card was switched; rings and card already show it. The owner persists it.
    internal event Action<UsageView>? UsageViewChanged;

    /// The gear button was pressed: the owner opens the settings.
    internal event Action? SettingsRequested;

    /// The panel's close button was pressed: quit the application.
    internal event Action? CloseRequested;

    /// The Claude ring was clicked: renew the session so the ring stops reading "--".
    internal event Action? ClaudeClicked;

    internal EdgeWindow(DateTime sessionStart, PanelMode mode)
    {
        _sessionStart = sessionStart;
        _mode = mode;
        InitializeComponent();

        _ringItems = new FrameworkElement[] { ClaudeItem, CodexItem, CursorItem, OpenCodeItem, DeepSeekItem, OpenRouterItem, CpuItem, GpuItem };
        _rings = new[] { ClaudeRing, CodexRing, CursorRing, OpenCodeRing, DeepSeekRing, OpenRouterRing, CpuRing, GpuRing };
        _cards = new FrameworkElement[] { ClaudeCard, CodexCard, CursorCard, OpenCodeCard, DeepSeekCard, OpenRouterCard, CpuCard, GpuCard };
        ApplyRingBrushes();

        Left = -32000;
        Top = -32000;

        SyncModeButton();
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
        RefreshUsage();
        if (_openCode is not null) SetOpenCode(_openCode);
        if (_deepSeek is not null) SetDeepSeek(_deepSeek);
        if (_openRouter is not null) SetOpenRouter(_openRouter);
        if (_cpu is not null) SetCpu(_cpu);
        if (_gpu is not null) SetGpu(_gpu);
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
        CpuRing.RingBrush = GpuRing.RingBrush = Palette.Low;
    }

    private void OnClaudeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Trace("Edge", "claude ring clicked");
        ClaudeClicked?.Invoke();
    }

    /// Shown on the Claude card while the session is being renewed.
    internal void SetClaudeRenewing()
    {
        ClearRing(ClaudeRing, ClaudeLabel, "…");
        ClaudeMetrics.Visibility = Visibility.Collapsed;
        ClaudeTabs.Visibility = Visibility.Collapsed;
        ClaudeMessage.Text = Loc.Get("Value.Renewing");
        ClaudeMessage.Visibility = Visibility.Visible;
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// The renewal could not start: puts the last reading back and says why on the card, instead of leaving
    /// "Renovando la sesión…" behind. The next usage refresh replaces the message.
    internal void SetClaudeRenewFailed(string message)
    {
        if (_claude is not null) SetClaude(_claude);
        else ClearRing(ClaudeRing, ClaudeLabel);
        ClaudeMessage.Text = Loc.Message(message);
        ClaudeMessage.Visibility = Visibility.Visible;
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
    /// Either way it never exceeds what fits the panel with all eight rings, so nothing is ever cut off.
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

    /// Height of the panel (design units) with every ring shown, whatever is visible right now, so the scale does
    /// not jump when a provider appears.
    private double FullPanelHeight()
    {
        var saved = new Visibility[_ringItems.Length];
        for (int i = 0; i < _ringItems.Length; i++)
        {
            saved[i] = _ringItems[i].Visibility;
            _ringItems[i].Visibility = Visibility.Visible;
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

    /// Three buttons in one row if each would be at least MinRowButtonDip wide on screen, else two plus one below.
    private void LayOutButtons()
    {
        bool row = RowButtonDiameter * _scale >= MinRowButtonDip;
        double diameter = row ? RowButtonDiameter : StackedButtonDiameter;
        double gap = row ? ButtonGap : StackedButtonGap;

        Panel target = row ? ButtonRowTop : ButtonRowBottom;
        if (CloseButton.Parent != target)
        {
            ((Panel)CloseButton.Parent).Children.Remove(CloseButton);
            target.Children.Add(CloseButton);
        }

        ModeButton.Margin = new Thickness(0);
        SettingsButton.Margin = new Thickness(gap, 0, 0, 0);
        CloseButton.Margin = row ? new Thickness(gap, 0, 0, 0) : new Thickness(0, gap, 0, 0);
        foreach (Button button in new[] { ModeButton, SettingsButton, CloseButton })
        {
            button.Width = button.Height = diameter;
            if (button.Content is FrameworkElement icon) icon.Width = icon.Height = Math.Round(diameter * 0.5);
        }
    }

    internal bool ButtonsInOneRow => CloseButton.Parent == ButtonRowTop;

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

            if (snapshot.Spent is Money spent)
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
            if (snapshot.OnDemand is UsageWindow onDemand)
            {
                SetPercentBar(CursorOnDemandBar, onDemand.Percent);
                CursorOnDemandValue.Text = Fmt.Used(onDemand.Percent);
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
            Animate(OpenCodeRing, RingGauge.ValueProperty, 0, 300);
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
            Animate(OpenCodeRing, RingGauge.ValueProperty, 0, 300);
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
            Animate(DeepSeekRing, RingGauge.ValueProperty, 0, 300);
            DeepSeekBalance.Text = Fmt.Amount(balance);
            DeepSeekMetrics.Visibility = Visibility.Visible;
            DeepSeekMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            DeepSeekLabel.Text = "--";
            Animate(DeepSeekRing, RingGauge.ValueProperty, 0, 300);
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
            if (snapshot.Message == "PawnIO no está instalado") { ClearRing(CpuRing, CpuLabel, Loc.Get("Ring.InstallPawnIo")); SetPawnIoLabel(CpuLabel); }
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
            if (snapshot.Message == "PawnIO no está instalado") { ClearRing(GpuRing, GpuLabel, Loc.Get("Ring.InstallPawnIo")); SetPawnIoLabel(GpuLabel); }
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

    private void SetRingVisible(int index, bool visible)
    {
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        FrameworkElement item = _ringItems[index];
        if (item.Visibility == target) return;
        Log.Trace("Edge", $"ring {index} {(visible ? "shown" : "hidden")}");

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

        // The first visible ring carries no top margin, so the panel starts at the same place whichever it is.
        bool first = true;
        foreach (FrameworkElement ringItem in _ringItems)
        {
            if (ringItem.Visibility != Visibility.Visible) continue;
            ringItem.Margin = new Thickness(0, first ? 0 : 10, 0, 0);
            first = false;
        }

        CenterPanel(animate: _expanded);
        if (_cardVisible) PlaceCard(animate: true);
    }

    private void RefreshTimeTexts()
    {
        var now = DateTimeOffset.Now;
        ClaudeHeaderReset.Text = _claude?.Session is UsageWindow session ? Fmt.Reset(session.ResetsAt, now) : string.Empty;
        WeeklyReset.Text = _claude?.Weekly is UsageWindow weekly ? Fmt.Reset(weekly.ResetsAt, now) : string.Empty;
        CodexReset.Text = _codexShown is CodexWindow shown ? Fmt.Reset(shown.ResetsAt, now) : string.Empty;
        CursorHeaderReset.Text = _cursor?.Cycle is UsageWindow cursorCycle ? Fmt.Reset(cursorCycle.ResetsAt, now) : string.Empty;
        CpuSince.Text = Loc.Format("Card.Since", _sessionStart);
    }

    /// Usage ring: the provider's colour; above Palette.AlertPercent both the arc and the percentage turn red.
    private void SetUsageRing(RingGauge ring, TextBlock label, SolidColorBrush brand, double percent)
    {
        ring.RingBrush = Palette.ForRing(brand, percent);
        Animate(ring, RingGauge.ValueProperty, Fraction(percent), 600);
        label.Text = Fmt.Percent(percent);
        SetLabelAlert(label, Palette.IsAlert(percent));
    }

    /// Temperature ring: low / medium / red by band, and the reading turns red with the arc.
    private void SetTemperatureRing(RingGauge ring, TextBlock label, double celsius)
    {
        SolidColorBrush brush = Palette.ForTemperature(celsius);
        ring.RingBrush = brush;
        Animate(ring, RingGauge.ValueProperty, Fraction(celsius), 600);
        label.Text = Fmt.Celsius(celsius);
        SetLabelAlert(label, brush == Palette.Red);
    }

    private void ClearRing(RingGauge ring, TextBlock label, string text = "--")
    {
        Animate(ring, RingGauge.ValueProperty, 0, 300);
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
        Animate(bar, LinearBar.ValueProperty, Fraction(percent), 500);
    }

    private void SetTemperatureBar(LinearBar bar, double celsius)
    {
        bar.Fill = Palette.ForTemperature(celsius);
        Animate(bar, LinearBar.ValueProperty, Fraction(celsius), 500);
    }

    private void ClearBar(LinearBar bar) => Animate(bar, LinearBar.ValueProperty, 0, 300);

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

        // Poll quickly only when the pointer is over the strip, panel or visible card; ignore the empty window area.
        bool near = !_hasWindowRect || NearInteractiveArea(cursor);
        SetPollInterval(near ? NearPoll : FarPoll);
        if (!near)
        {
            if (_expanded) HandleCursorAway();
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
        }

        Animate(PanelShift, TranslateTransform.XProperty, 0, PanelMs);
        Animate(Strip, OpacityProperty, 0, 150);
        ExpandedChanged?.Invoke(true);
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        Log.Trace("Edge", "collapse");

        _outside.Reset();
        _hoveredRing = -1;
        if (!PreviewMode) SetClickThrough(true);

        HideCard();
        for (int i = 0; i < _rings.Length; i++) ScaleRing(i, 1.0);
        Animate(PanelShift, TranslateTransform.XProperty, PanelWidth, PanelMs);
        Animate(Strip, OpacityProperty, 1, PanelMs);
        ExpandedChanged?.Invoke(false);
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

    private void Animate(IAnimatable target, DependencyProperty property, double to, int milliseconds, double? from = null)
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

        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = EaseOut };
        if (from is double start) animation.From = start;
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static IEasingFunction CreateEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
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

    /// Renders the window at 2× over a wallpaper-like backdrop (teal top, blue middle, warm bottom).
    internal void SaveSnapshot(string path)
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
        bitmap.Render(backdrop);
        bitmap.Render(Host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
