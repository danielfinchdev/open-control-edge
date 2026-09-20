using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EdgeWidget.Services;
using EdgeWidget.Ui;
using static EdgeWidget.Interop.NativeMethods;

namespace EdgeWidget.Views;

/// One transparent, topmost tool window glued to the right edge of the primary screen. It hosts the
/// collapsed strip, the ring panel and the detail card, so the card can glide between rings with render
/// animations instead of moving HWNDs.
///
/// Two modes: Pinned (panel always expanded, default) and Auto (collapses to a strip, expands on hover).
/// The panel carries a mode button ("Ocultar" / "Fijar") and a close button. The Codex and GPU rings can come and go.
/// Hover is driven by polling the cursor position: while collapsed the window is click-through
/// (WS_EX_TRANSPARENT) and receives no mouse input at all, so events could not detect the strip.
public partial class EdgeWindow : Window
{
    internal const int RingClaude = 0;
    internal const int RingCodex = 1;
    internal const int RingGrok = 2;
    internal const int RingCpu = 3;
    internal const int RingGpu = 4;

    // Geometry in DIPs — the original design scaled to 85 %.
    private const double PanelWidth = 94;
    private const double StripWidth = 5;
    private const double StripHeight = 400;
    private const double CardBodyWidth = 255;
    private const double BeakLength = 8.5;
    private const double CardGap = 8.5;
    private const double CardSideRoom = 34;      // room for the card shadow on the left
    private const double CardVerticalRoom = 41;  // keep the card (and its shadow) inside the window
    private const double WindowWidthDip = CardSideRoom + CardBodyWidth + BeakLength + CardGap + PanelWidth;
    private const double WindowHeightDip = 600;

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
    private RECT _windowRect;
    private bool _hasWindowRect;
    private ClaudeSnapshot? _claude;
    private CodexSnapshot? _codex;
    private GrokSnapshot? _grok;

    internal bool AnimationsEnabled { get; set; } = true;

    /// Snapshot rendering: no positioning, no topmost juggling, no pointer polling.
    internal bool PreviewMode { get; set; }

    internal event Action<bool>? ExpandedChanged;

    /// The panel's "Actualizar" button was pressed: refresh CPU, GPU, Claude and Codex (the owner calls SetRefreshing).
    internal event Action? RefreshRequested;

    /// "Ocultar" (pinned → auto) or "Fijar" (auto → pinned) was pressed. The owner persists it and calls ApplyMode.
    internal event Action<PanelMode>? ModeChangeRequested;

    /// The panel's close button was pressed: quit the application.
    internal event Action? CloseRequested;

    /// The Claude ring was clicked: renew the session so the ring stops reading "--".
    internal event Action? ClaudeClicked;

    internal EdgeWindow(DateTime sessionStart, PanelMode mode)
    {
        _sessionStart = sessionStart;
        _mode = mode;
        InitializeComponent();

        _ringItems = new FrameworkElement[] { ClaudeItem, CodexItem, GrokItem, CpuItem, GpuItem };
        _rings = new[] { ClaudeRing, CodexRing, GrokRing, CpuRing, GpuRing };
        _cards = new FrameworkElement[] { ClaudeCard, CodexCard, GrokCard, CpuCard, GpuCard };

        Width = WindowWidthDip;
        Height = WindowHeightDip;
        Left = -32000;
        Top = -32000;

        Canvas.SetLeft(Strip, WindowWidthDip - StripWidth);
        Canvas.SetTop(Strip, (WindowHeightDip - StripHeight) / 2);

        SyncModeButton();
        Canvas.SetLeft(EdgePanel, WindowWidthDip - PanelWidth);
        CenterPanel(animate: false);

        Canvas.SetLeft(Card, CardSideRoom);
        Card.Width = CardBodyWidth + BeakLength;
        Card.Height = 0;

        _pointerWatch = new DispatcherTimer(DispatcherPriority.Input) { Interval = FarPoll };
        _pointerWatch.Tick += OnPointerTick;

        SourceInitialized += OnSourceInitialized;
        RefreshTimeTexts();
    }

    // ───────────────────────────── Mode & panel buttons ─────────────────────────────

    internal void ApplyMode(PanelMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Log.Trace("Edge", $"mode -> {mode}");

        if (mode == PanelMode.Auto)
        {
            // The label keeps saying "Ocultar" while the panel slides out; it is refreshed on the next expand.
            Collapse();
        }
        else if (_expanded)
        {
            // "Fijar" pressed on a hover-expanded panel: it simply stays open.
            SyncModeButton();
        }
        else
        {
            Expand();
        }
    }

    /// Same button, same place: "Ocultar" when pinned, "Fijar" in auto mode.
    private void SyncModeButton() =>
        ModeButton.Content = _mode == PanelMode.Pinned ? "Ocultar" : "Fijar";

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        PanelMode target = _mode == PanelMode.Pinned ? PanelMode.Auto : PanelMode.Pinned;
        Log.Trace("Edge", $"mode button clicked -> {target}");
        ModeChangeRequested?.Invoke(target);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Log.Trace("Edge", "close button clicked");
        CloseRequested?.Invoke();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        Log.Trace("Edge", "refresh button clicked");
        RefreshRequested?.Invoke();
    }

    private void OnClaudeClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log.Trace("Edge", "claude ring clicked");
        ClaudeClicked?.Invoke();
    }

    /// Shown on the Claude card while the session is being renewed.
    internal void SetClaudeRenewing()
    {
        ClaudeLabel.Text = "…";
        ClaudeMetrics.Visibility = Visibility.Collapsed;
        ClaudeMessage.Text = "Renovando la sesión…";
        ClaudeMessage.Visibility = Visibility.Visible;
        if (_cardVisible) PlaceCard(animate: true);
    }

    /// While a full refresh runs the button reads "Actualizando" and is disabled.
    internal void SetRefreshing(bool refreshing)
    {
        RefreshButton.Content = refreshing ? "Actualizando" : "Actualizar";
        RefreshButton.IsEnabled = !refreshing;
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
        _panelTop = Math.Round((WindowHeightDip - EdgePanel.DesiredSize.Height) / 2);
        Animate(EdgePanel, Canvas.TopProperty, _panelTop, animate ? PanelMs : 0);
    }

    // ───────────────────────────── Data ─────────────────────────────

    internal void SetClaude(ClaudeSnapshot snapshot)
    {
        _claude = snapshot;

        if (snapshot.Session is UsageWindow session)
        {
            Animate(ClaudeRing, RingGauge.ValueProperty, Fraction(session.Percent), 600);
            ClaudeLabel.Text = Fmt.Percent(session.Percent);

            SetPercentBar(SessionBar, session.Percent);
            SessionValue.Text = $"{Fmt.Percent(session.Percent)} usado";

            if (snapshot.Weekly is UsageWindow weekly)
            {
                SetPercentBar(WeeklyBar, weekly.Percent);
                WeeklyValue.Text = $"{Fmt.Percent(weekly.Percent)} usado";
            }
            else
            {
                ClearBar(WeeklyBar);
                WeeklyValue.Text = "--";
            }

            if (snapshot.Spent is Money spent)
            {
                ClaudeSpendValue.Text = Fmt.Amount(spent);
                ClaudeSpendRow.Visibility = Visibility.Visible;
            }
            else
            {
                ClaudeSpendRow.Visibility = Visibility.Collapsed;
            }

            ClaudeMetrics.Visibility = Visibility.Visible;
            ClaudeMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            Animate(ClaudeRing, RingGauge.ValueProperty, 0, 300);
            ClaudeLabel.Text = "--";
            ClaudeMetrics.Visibility = Visibility.Collapsed;
            ClaudeMessage.Text = snapshot.Message ?? "Sin datos";
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

        if (snapshot.Primary is CodexWindow primary)
        {
            CodexRing.RingBrush = Palette.ForPercent(primary.Percent);
            Animate(CodexRing, RingGauge.ValueProperty, Fraction(primary.Percent), 600);
            CodexLabel.Text = Fmt.Percent(primary.Percent);

            CodexPrimaryLabel.Text = Fmt.WindowLabel(primary.Length);
            SetPercentBar(CodexPrimaryBar, primary.Percent);
            CodexPrimaryValue.Text = $"{Fmt.Percent(primary.Percent)} usado";

            if (snapshot.Secondary is CodexWindow secondary)
            {
                CodexSecondaryLabel.Text = Fmt.WindowLabel(secondary.Length);
                SetPercentBar(CodexSecondaryBar, secondary.Percent);
                CodexSecondaryValue.Text = $"{Fmt.Percent(secondary.Percent)} usado";
                CodexSecondaryRow.Visibility = Visibility.Visible;
            }
            else
            {
                CodexSecondaryRow.Visibility = Visibility.Collapsed;
            }

            CodexMetrics.Visibility = Visibility.Visible;
            CodexMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            Animate(CodexRing, RingGauge.ValueProperty, 0, 300);
            CodexLabel.Text = "--";
            CodexMetrics.Visibility = Visibility.Collapsed;
            CodexMessage.Text = snapshot.Message ?? "Sin datos";
            CodexMessage.Visibility = Visibility.Visible;
        }

        RefreshTimeTexts();
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetGrok(GrokSnapshot snapshot)
    {
        _grok = snapshot;
        SetRingVisible(RingGrok, !snapshot.Hidden);
        if (snapshot.Hidden) return;

        if (snapshot.Weekly is GrokWindow weekly)
        {
            GrokRing.RingBrush = Palette.ForPercent(weekly.Percent);
            Animate(GrokRing, RingGauge.ValueProperty, Fraction(weekly.Percent), 600);
            GrokLabel.Text = Fmt.Percent(weekly.Percent);

            SetPercentBar(GrokWeeklyBar, weekly.Percent);
            GrokWeeklyValue.Text = $"{Fmt.Percent(weekly.Percent)} usado";

            if (snapshot.OnDemand is GrokWindow onDemand)
            {
                SetPercentBar(GrokOnDemandBar, onDemand.Percent);
                GrokOnDemandValue.Text = $"{Fmt.Percent(onDemand.Percent)} usado";
                GrokOnDemandRow.Visibility = Visibility.Visible;
            }
            else
            {
                GrokOnDemandRow.Visibility = Visibility.Collapsed;
            }

            GrokMetrics.Visibility = Visibility.Visible;
            GrokMessage.Visibility = Visibility.Collapsed;
        }
        else
        {
            Animate(GrokRing, RingGauge.ValueProperty, 0, 300);
            GrokLabel.Text = "--";
            GrokMetrics.Visibility = Visibility.Collapsed;
            GrokMessage.Text = snapshot.Message ?? "Sin datos";
            GrokMessage.Visibility = Visibility.Visible;
        }

        RefreshTimeTexts();
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetCpu(CpuSnapshot snapshot)
    {
        if (snapshot.Temperature is double temperature)
        {
            CpuRing.RingBrush = Palette.ForTemperature(temperature);
            Animate(CpuRing, RingGauge.ValueProperty, Fraction(temperature), 600);
            CpuLabel.Text = Fmt.Celsius(temperature);
            SetTemperatureBar(TempBar, temperature);
            TempValue.Text = Fmt.Celsius(temperature);
        }
        else
        {
            Animate(CpuRing, RingGauge.ValueProperty, 0, 300);
            CpuLabel.Text = "--";
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
            LoadValue.Text = $"{Fmt.Percent(load)} en uso";
        }
        else
        {
            ClearBar(LoadBar);
            LoadValue.Text = "--";
        }

        CpuTitle.Text = ShortHardwareName(snapshot.Name, "CPU");
        CpuMessage.Text = snapshot.Message ?? string.Empty;
        CpuMessage.Visibility = snapshot.Message is null ? Visibility.Collapsed : Visibility.Visible;

        RefreshTimeTexts();
        if (_cardVisible) PlaceCard(animate: true);
    }

    internal void SetGpu(GpuSnapshot snapshot)
    {
        SetRingVisible(RingGpu, snapshot.Detected);
        if (!snapshot.Detected) return;

        if (snapshot.Temperature is double temperature)
        {
            GpuRing.RingBrush = Palette.ForTemperature(temperature);
            Animate(GpuRing, RingGauge.ValueProperty, Fraction(temperature), 600);
            GpuLabel.Text = Fmt.Celsius(temperature);
            SetTemperatureBar(GpuTempBar, temperature);
            GpuTempValue.Text = Fmt.Celsius(temperature);
        }
        else
        {
            Animate(GpuRing, RingGauge.ValueProperty, 0, 300);
            GpuLabel.Text = "--";
            ClearBar(GpuTempBar);
            GpuTempValue.Text = "--";
        }

        if (snapshot.Load is double load)
        {
            SetPercentBar(GpuLoadBar, load);
            GpuLoadValue.Text = $"{Fmt.Percent(load)} en uso";
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
                GpuMemoryValue.Text = $"{Fmt.Megabytes(used)} de {Fmt.Megabytes(total)}";
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
        GpuMessage.Text = snapshot.Message ?? string.Empty;
        GpuMessage.Visibility = snapshot.Message is null ? Visibility.Collapsed : Visibility.Visible;

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

        CenterPanel(animate: _expanded);
        if (_cardVisible) PlaceCard(animate: true);
    }

    private void RefreshTimeTexts()
    {
        var now = DateTimeOffset.Now;
        ClaudeHeaderReset.Text = _claude?.Session is UsageWindow session ? Fmt.Reset(session.ResetsAt, now) : string.Empty;
        WeeklyReset.Text = _claude?.Weekly is UsageWindow weekly ? Fmt.Reset(weekly.ResetsAt, now) : string.Empty;
        CodexHeaderReset.Text = _codex?.Primary is CodexWindow primary ? Fmt.Reset(primary.ResetsAt, now) : string.Empty;
        CodexSecondaryReset.Text = _codex?.Secondary is CodexWindow secondary ? Fmt.Reset(secondary.ResetsAt, now) : string.Empty;
        GrokHeaderReset.Text = _grok?.Weekly is GrokWindow grokWeekly ? Fmt.Reset(grokWeekly.ResetsAt, now) : string.Empty;
        CpuSince.Text = $"desde las {_sessionStart:HH:mm}";
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

        // Cheap gate first: while the cursor is anywhere else on the desktop — almost always — a tick costs
        // four integer comparisons and nothing else. WPF hit-testing (PointFromScreen walks the visual tree
        // and validates layout) only runs once the cursor is actually over this window.
        bool near = !_hasWindowRect || Contains(_windowRect, cursor);
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

        // The close button overlaps the top of the first ring's row; hovering it must not open a card.
        int ring = -1;
        if (!Contains(CloseButton, screen))
        {
            for (int i = 0; i < _ringItems.Length; i++)
            {
                if (_ringItems[i].Visibility == Visibility.Visible && Contains(_ringItems[i], screen))
                {
                    ring = i;
                    break;
                }
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

    /// Physical pixels, as GetCursorPos and SetWindowPos both report them.
    private static bool Contains(RECT rect, POINT point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

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
            Animate(CardShift, TranslateTransform.XProperty, 0, 220, from: 10);
            Animate(Card, OpacityProperty, 1, 180);
        }
    }

    private void HideCard()
    {
        if (!_cardVisible) return;
        Log.Trace("Edge", "hide card");
        _cardVisible = false;
        _activeRing = -1;
        Animate(Card, OpacityProperty, 0, 150);
    }

    /// Centres the card on the active ring (clamped to the window) and points the beak at the ring.
    private void PlaceCard(bool animate)
    {
        if (_activeRing < 0) return;

        // Flush pending layout first: a bare Measure() on an ancestor returns a cached size when only a
        // descendant changed (e.g. a message line collapsed), which left the card too tall.
        UpdateLayout();
        CardContent.Measure(new Size(CardBodyWidth, double.PositiveInfinity));
        double height = Math.Ceiling(CardContent.DesiredSize.Height);

        // Measured against the panel's target top, so the card aims at where the ring ends up while the panel is
        // still re-centring after a ring appeared or disappeared.
        FrameworkElement ring = _rings[_activeRing];
        double ringCenter = _panelTop + ring.TranslatePoint(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2), EdgePanel).Y;

        double maxTop = Math.Max(CardVerticalRoom, WindowHeightDip - CardVerticalRoom - height);
        double top = Math.Min(Math.Max(ringCenter - height / 2, CardVerticalRoom), maxTop);
        double beakCenter = ringCenter - top;

        int ms = animate ? CardSlideMs : 0;
        Animate(Card, Canvas.TopProperty, top, ms);
        Animate(Card, HeightProperty, height, ms);
        Animate(CardBackground, CardShape.BeakCenterProperty, beakCenter, ms);
    }

    private void Animate(IAnimatable target, DependencyProperty property, double to, int milliseconds, double? from = null)
    {
        if (!AnimationsEnabled || milliseconds <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }

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
            PositionOnPrimaryScreen();
            SetClickThrough(true);
            _tickClock.Start();
            _pointerWatch.Start();
        }

        if (_mode == PanelMode.Pinned) Expand();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!PreviewMode && (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED))
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PositionOnPrimaryScreen));
        return IntPtr.Zero;
    }

    private void SetClickThrough(bool enabled)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (enabled) AddExtendedStyle(hwnd, WS_EX_TRANSPARENT);
        else AddExtendedStyle(hwnd, 0, WS_EX_TRANSPARENT);
    }

    /// Right edge of the primary monitor, vertically centred, in physical pixels.
    private void PositionOnPrimaryScreen()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
                ? dpiX / 96.0
                : VisualTreeHelper.GetDpi(this).DpiScaleX;

            RECT bounds = info.rcMonitor;
            int width = (int)Math.Round(WindowWidthDip * scale);
            int height = Math.Min((int)Math.Round(WindowHeightDip * scale), bounds.Bottom - bounds.Top);
            int x = bounds.Right - width;
            int y = bounds.Top + (bounds.Bottom - bounds.Top - height) / 2;

            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);

            // Cached for the pointer poll's fast path; re-cached on every reposition (display or DPI change).
            _windowRect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
            _hasWindowRect = true;
            Log.Trace("Edge", $"positioned at {x},{y} {width}x{height} (scale {scale})");
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

    /// Renders the window content at 2× over a gradient backdrop (dark top, light bottom).
    internal void SaveSnapshot(string path)
    {
        UpdateLayout();
        const double scale = 2;
        var bitmap = new RenderTargetBitmap(
            (int)(WindowWidthDip * scale), (int)(WindowHeightDip * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (DrawingContext dc = backdrop.RenderOpen())
        {
            var gradient = new LinearGradientBrush(Color.FromRgb(0x1B, 0x24, 0x3A), Color.FromRgb(0xE9, 0xD8, 0xE4), 90);
            dc.DrawRectangle(gradient, null, new Rect(0, 0, WindowWidthDip, WindowHeightDip));
        }
        bitmap.Render(backdrop);
        bitmap.Render(Root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
