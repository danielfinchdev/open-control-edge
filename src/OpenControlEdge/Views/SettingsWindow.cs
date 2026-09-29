using System.Diagnostics;
using System.Collections.Frozen;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using static OpenControlEdge.Interop.NativeMethods;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;
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
    private string _category = "General";
    private UpdateRelease? _release;
    private bool _noUpdateAvailable;
    private AiProviderId? _keyEditor;
    private static readonly string[] Categories = ["General", "Personalización", "Agentes", "Información", "Actualizaciones", "Feedback"];

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
        Width = 780; Height = 580; WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; UseLayoutRounding = true;
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
        var root = new DockPanel();

        var header = new DockPanel { Margin = new Thickness(20, 12, 12, 12), Background = Brushes.Transparent };
        var close = new Button { Style = (Style)Application.Current.FindResource("PanelRoundButton"), Width = 30, Height = 30,
            Content = Glyph(Icons.Close, 14), ToolTip = T("Cerrar", "Close"), VerticalAlignment = VerticalAlignment.Center };
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
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20, 16, 8, 14), Content = _content };
        root.Children.Add(scroll); frame.Child = root; Content = frame;
        ThemeManager.Changed += OnThemeChanged; Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        Loc.Changed += OnLanguageChanged; Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        Loaded += (_, _) => CenterOnWidgetMonitor();
        RenderPage();
    }

    private void CenterOnWidgetMonitor()
    {
        if (Owner is null) return;
        try
        {
            IntPtr hwnd = new WindowInteropHelper(Owner).Handle;
            IntPtr monitor = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
                : MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
            double dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1;
            Left = (info.rcWork.Left + info.rcWork.Right) / (2 * dpi) - Width / 2;
            Top = (info.rcWork.Top + info.rcWork.Bottom) / (2 * dpi) - Height / 2;
        }
        catch (Exception ex) { Log.Warn("Settings", "monitor centering: " + ex.GetType().Name); }
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
    internal void RefreshAgents() => Dispatcher.InvokeAsync(RenderPage);
    internal void ShowUpdateResult(UpdateRelease release)
    {
        _release = release;
        _noUpdateAvailable = false;
        _category = "Actualizaciones";
        RenderPage();
        Activate();
    }
    private static string T(string es, string en) => Loc.Language == UiLanguage.English ? en : es;
    private static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);
    private string Category(string c) => c switch { "Personalización" => T(c, "Appearance"), "Agentes" => T(c, "Agents"), "Información" => T(c, "About"), "Actualizaciones" => T(c, "Updates"), "Feedback" => c, _ => T(c, "General") };
    private string CategoryHint(string c) => c switch
    {
        "Personalización" => T("Idioma, tema, colores de los anillos y tamaño del widget.", "Language, theme, ring colours and widget size."),
        "Agentes" => T("Qué IAs aparecen en el panel y el estado de su conexión.", "Which AIs appear in the panel and how they are connected."),
        "Información" => T("Versión, sensores de temperatura y licencias.", "Version, temperature sensors and licenses."),
        "Actualizaciones" => T("Busca e instala versiones nuevas verificadas.", "Find and install verified new versions."),
        "Feedback" => T("Cuéntanos un error o una idea.", "Report a bug or share an idea."),
        _ => T("Arranque, modo del panel y frecuencia de actualización.", "Startup, panel mode and refresh rate."),
    };
    private static Drawing CategoryIcon(string c) => c switch
    {
        "General" => Icons.Gear, "Personalización" => Icons.Swatch, "Agentes" => Icons.Bot,
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
        _title.Text = "Open Control Edge · " + T("Ajustes", "Settings");
        foreach (Button button in _navigation.Children)
        {
            string category = (string)button.Tag;
            bool active = category == _category;
            if (active) button.SetResourceReference(BackgroundProperty, "Set.Secondary"); else button.ClearValue(BackgroundProperty);
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(Glyph(CategoryIcon(category), 16, active ? "Set.Foreground" : "Set.Muted"));
            var text = new TextBlock { Text = Category(category), Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal };
            text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Set.Foreground" : "Set.Muted");
            item.Children.Add(text);
            button.Content = item;
        }
        _content.Children.Clear();
        AddHeading(Category(_category), CategoryHint(_category));
        switch (_category) { case "General": General(); break; case "Personalización": Appearance(); break; case "Agentes": Agents(); break; case "Información": About(); break; case "Actualizaciones": Updates(); break; case "Feedback": Feedback(); break; }
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
    private static DockPanel Row(Panel panel, string label, UIElement control, string? hint = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
        DockPanel.SetDock(control, Dock.Right); row.Children.Add(control);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(label));
        if (hint is not null) text.Children.Add(Muted(hint, new Thickness(0, 2, 16, 0), 12));
        row.Children.Add(text); panel.Children.Add(row); return row;
    }
    private static void Store(Func<Settings, Settings> update) => SettingsStore.Update(update);
    private void General()
    {
        Settings s = SettingsStore.Load(); var card = Card(T("Preferencias", "Preferences")); var p = Inside(card);
        AddToggle(p, T("Iniciar con Windows", "Start with Windows"), AutoStartService.IsEnabled(), enabled =>
        {
            string? error = enabled ? AutoStartService.Enable() : AutoStartService.Disable();
            if (error != null) MessageBox.Show(DisplayError(error));
        });
        AddChoice(p, T("Modo del panel", "Panel mode"), ["Fijado", "Automático"], s.PanelMode == PanelMode.Auto ? 1 : 0, i => { var updated = SettingsStore.Update(x => x with { PanelMode = i == 0 ? PanelMode.Pinned : PanelMode.Auto }); if (updated != null) _apply(updated); });
        int[] intervals = [2, 5, 10, 15];
        AddChoice(p, T("Intervalo de actualización", "Usage refresh interval"), intervals.Select(i => $"{i} min").ToArray(),
            Array.IndexOf(intervals, s.UsageRefreshMinutes), i => { var updated = SettingsStore.Update(x => x with { UsageRefreshMinutes = intervals[i] }); if (updated != null) _apply(updated); });
        var lastValue = _lastRefresh();
        var last = Muted(T("Última actualización: ", "Last updated: ") + (lastValue?.ToLocalTime().ToString("t", Loc.Culture) ?? "—"), new Thickness(0, 6, 0, 0));
        p.Children.Add(last);
        Actions(p, Button(T("Refrescar ahora", "Refresh now"), async (_, _) =>
        {
            last.Text = T("Actualizando…", "Refreshing…");
            await _refresh();
            last.Text = T("Última actualización: ", "Last updated: ") + (_lastRefresh()?.ToLocalTime().ToString("t", Loc.Culture) ?? DateTime.Now.ToString("t", Loc.Culture));
        }, true));

        var extra = Card(T("Opciones", "Options")); var ep = Inside(extra);
        AddToggle(ep, T("Renovar la sesión de Claude automáticamente", "Automatically renew Claude session"), s.AutoRenewClaude, v => Store(x => x with { AutoRenewClaude = v }));
        AddToggle(ep, T("Permitir liberar RAM con un clic", "Allow one-click RAM cleanup"), s.RamCleanup, v => Store(x => x with { RamCleanup = v }));
        Actions(ep, Button(T("Restablecer ajustes", "Reset settings"), (_, _) =>
        {
            if (MessageBox.Show(T("¿Restablecer todos los ajustes?", "Reset all settings?"), "Open Control Edge",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Settings? reset = SettingsStore.Update(_ => Settings.Defaults);
            if (reset is null) return;
            ThemeManager.Apply(reset.Theme, reset.ColorTheme); Loc.Apply(reset.Language); _apply(reset); RenderPage();
        }, variant: "Secondary"));

        var danger = Inside(Card(T("Desinstalar", "Uninstall"), T("Quita Open Control Edge de este equipo. Se completa al reiniciar.", "Removes Open Control Edge from this PC. It completes after a restart.")));
        danger.Children.RemoveAt(danger.Children.Count - 1);
        Actions(danger, Button(T("Desinstalar…", "Uninstall…"), (_, _) => { if (MessageBox.Show(T("¿Desinstalar Open Control Edge?", "Uninstall Open Control Edge?"), "Open Control Edge", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { string? error = AutoStartService.Uninstall(); MessageBox.Show(error ?? T("Se desinstalará al reiniciar.", "It will be uninstalled after restart.")); } }));
    }
    private void Appearance()
    {
        Settings s = SettingsStore.Load(); var p = Inside(Card(T("Aspecto", "Appearance")));
        AddChoice(p, T("Idioma", "Language"), ["Español", "English"], s.Language == UiLanguage.English ? 1 : 0, i => { var n = SettingsStore.Update(x => x with { Language = i == 0 ? UiLanguage.Spanish : UiLanguage.English }); if (n != null) Loc.Apply(n.Language); });
        AddChoice(p, T("Tema", "Theme"), [T("Oscuro", "Dark"), T("Claro", "Light"), T("Sistema", "System")], (int)s.Theme, i => { var n = SettingsStore.Update(x => x with { Theme = (AppTheme)i }); if (n != null) ThemeManager.Apply(n.Theme, n.ColorTheme); });
        AddRingThemes(s);
        AddScale(s);
    }
    private void AddChoice(Panel panel, string label, string[] choices, int selected, Action<int> changed)
    {
        var combo = new ComboBox { Width = 180, ItemsSource = choices, SelectedIndex = selected, Style = StyleOf("Oce.Select"),
            ItemContainerStyle = StyleOf("Oce.Select.Item"), VerticalAlignment = VerticalAlignment.Center };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); };
        Row(panel, label, combo);
    }
    private static ToggleButton Switch(bool value, Action<bool> changed)
    {
        var toggle = new ToggleButton { Style = StyleOf("Oce.Switch"), IsChecked = value };
        toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }
    private void AddToggle(Panel panel, string text, bool value, Action<bool> changed) => Row(panel, text, Switch(value, changed));
    private void AddRingThemes(Settings s)
    {
        var card = Card(T("Color de los anillos", "Ring colors"), T("Paleta de los anillos del panel.", "Palette of the panel rings."));
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
        var card = Card(T("Tamaño del widget", "Widget size"));
        var slider = new Slider { Style = StyleOf("Oce.ScaleSlider"), Value = s.UiScale ?? 1, IsEnabled = s.UiScale is not null, Margin = new Thickness(0, 6, 0, 0) };
        string ScaleText() => T("Escala manual: ", "Manual scale: ") + $"{slider.Value:P0}";
        var auto = new ToggleButton { Style = StyleOf("Oce.Switch"), IsChecked = s.UiScale is null };
        var row = Row(Inside(card), T("Automático", "Automatic"), auto, s.UiScale is null ? T("Escala automática", "Automatic scale") : ScaleText());
        var value = (TextBlock)((StackPanel)row.Children[1]).Children[1];
        Inside(card).Children.Add(slider);
        auto.Checked += (_, _) => { slider.IsEnabled = false; value.Text = T("Escala automática", "Automatic scale"); var updated = SettingsStore.Update(x => x with { UiScale = null }); if (updated != null) _apply(updated); };
        auto.Unchecked += (_, _) => { slider.IsEnabled = true; value.Text = ScaleText(); var updated = SettingsStore.Update(x => x with { UiScale = slider.Value }); if (updated != null) _apply(updated); };
        slider.ValueChanged += (_, _) => { if (!slider.IsEnabled) return; value.Text = ScaleText(); var updated = SettingsStore.Update(x => x with { UiScale = slider.Value }); if (updated != null) _apply(updated); };
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
            var show = Switch(visible, on =>
            {
                var updated = SettingsStore.Update(x => { var map = x.Providers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase); map[name] = on ? ProviderVisibility.Show : ProviderVisibility.Hide; return x with { Providers = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) }; });
                if (updated != null) _apply(updated);
            });
            show.ToolTip = T("Mostrar en el panel", "Show in the panel");
            var showBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            showBox.Children.Add(Muted(T("Mostrar", "Show"), new Thickness(0, 0, 8, 1), 12.5));
            ((TextBlock)showBox.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            showBox.Children.Add(show);
            DockPanel.SetDock(showBox, Dock.Right); row.Children.Add(showBox);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (id is AiProviderId.DeepSeek or AiProviderId.OpenRouter)
                actions.Children.Add(SmallButton(T("Clave API", "API key"), (_, _) => { _keyEditor = _keyEditor == id ? null : id; RenderPage(); }));
            else if (id is AiProviderId.Claude or AiProviderId.Codex or AiProviderId.Cursor && status.Kind is "session" or "missing")
            {
                actions.Children.Add(SmallButton(T("Conectar", "Connect"), (_, _) =>
                {
                    string cli = id switch { AiProviderId.Claude => "claude", AiProviderId.Codex => "codex", _ => "cursor-agent" };
                    var result = UnelevatedLauncher.Run(cli, "login", Environment.CurrentDirectory, hidden: false, wait: null);
                    if (!result.Started) MessageBox.Show(DisplayError(result.Error ?? T("No se pudo iniciar el login.", "Could not start login.")));
                }));
            }
            if (status.Kind == "error" || (status.Kind == "session" && id is not (AiProviderId.Claude or AiProviderId.Codex or AiProviderId.Cursor)))
            {
                if (actions.Children.Count > 0) ((FrameworkElement)actions.Children[^1]).Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(SmallButton(T("Reintentar", "Retry"), async (_, _) => { await _retry(id); RenderPage(); }));
            }
            DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);

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
            row.Children.Add(text);
            p.Children.Add(row);

            if (_keyEditor == id) p.Children.Add(KeyEditor(id, name));
        }
    }
    private static Button SmallButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = StyleOf("Oce.Button.Small") };
        b.Click += click; return b;
    }
    /// Outline badge with a coloured dot: connected (and the plan), not installed, no session or sync error.
    private static Border StatusBadge(AgentStatus status)
    {
        (string text, string? dot) = status.Kind switch
        {
            "connected" => (T("Conectado", "Connected") + (string.IsNullOrWhiteSpace(status.Plan) ? "" : " · " + status.Plan), "Oce.Success"),
            "missing" => (T("No instalado", "Not installed"), null),
            "session" => (T("Sin sesión", "No session"), "Oce.Warning"),
            _ => (T("Error de sincronización", "Sync error"), "Oce.Danger"),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot is not null) content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = (Brush)Application.Current.FindResource(dot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, dot is null ? "Set.Muted" : "Set.Foreground");
        content.Children.Add(label);
        return new Border { Style = StyleOf("Oce.Badge"), Child = content };
    }
    /// API key field of DeepSeek / OpenRouter, opened from the row's "Clave API" button.
    private DockPanel KeyEditor(AiProviderId id, string name)
    {
        var editor = new DockPanel { Margin = new Thickness(46, 12, 0, 0) };
        var box = new PasswordBox { Style = StyleOf("Oce.Password"), ToolTip = T("Clave API de ", "API key for ") + name };
        var save = Button(T("Guardar", "Save"), async (_, _) =>
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(box.Password);
            bool ok = ProviderKeyStore.Save(name.ToLowerInvariant(), bytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            MessageBox.Show(ok ? T("Clave guardada", "Key saved") : T("No se pudo guardar", "Could not save"));
            if (ok) { _keyEditor = null; await _retry(id); RenderPage(); }
        }, true);
        var delete = Button(T("Borrar", "Delete"), async (_, _) => { ProviderKeyStore.Delete(name.ToLowerInvariant()); await _retry(id); RenderPage(); });
        save.Margin = new Thickness(8, 0, 0, 0); delete.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(delete, Dock.Right); DockPanel.SetDock(save, Dock.Right);
        editor.Children.Add(delete); editor.Children.Add(save); editor.Children.Add(box);
        return editor;
    }
    private void About()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "2.1.0";
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        var p = Inside(Card("Open Control Edge", $"{version} · {build} · .NET 8 · win-x64"));
        bool pawnInstalled = false; try { pawnInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { }
        bool pawnRunning = pawnInstalled && PawnIoInstaller.ServiceRunning;
        var pawnState = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pawnState.Children.Add(new Ellipse { Style = StyleOf(pawnRunning ? "Oce.Led.On" : "Oce.Led"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
        pawnState.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center,
            Text = pawnRunning ? T("Instalado y en marcha", "Installed and running") : pawnInstalled ? T("Instalado, servicio detenido", "Installed, service stopped") : T("Falta", "Missing") });
        UIElement pawnControl = pawnState;
        if (!pawnInstalled)
        {
            var install = new StackPanel { Orientation = Orientation.Horizontal };
            install.Children.Add(pawnState); pawnState.Margin = new Thickness(0, 0, 12, 0);
            install.Children.Add(SmallButton(T("Descargar e instalar", "Download and install"), async (sender, _) => { var b = (Button)sender; b.IsEnabled = false; b.Content = T("Verificando e instalando…", "Verifying and installing…"); string? error = await PawnIoInstaller.InstallAsync(); MessageBox.Show(error is null ? T("PawnIO instalado.", "PawnIO installed.") : DisplayError(error)); RenderPage(); }));
            pawnControl = install;
        }
        Row(p, "PawnIO", pawnControl, T("Controlador para leer las temperaturas de CPU y GPU.", "Driver used to read CPU and GPU temperatures."));
        bool sensors = _sensorsAvailable();
        Row(p, T("Lecturas de temperatura disponibles", "Temperature readings available"), new TextBlock { Text = T(sensors ? "Sí" : "No", sensors ? "Yes" : "No"), VerticalAlignment = VerticalAlignment.Center });
        var links = new[] { (T("Repositorio", "Repository"), "https://github.com/danielfinchdev/open-control-edge"), (T("Licencia", "License"), "https://github.com/danielfinchdev/open-control-edge/blob/v2.1/LICENSE"), (T("Licencias de terceros", "Third-party licenses"), "https://github.com/danielfinchdev/open-control-edge/blob/v2.1/THIRD-PARTY-NOTICES.txt") }
            .Select(link => Button(link.Item1, (_, _) => Process.Start(new ProcessStartInfo(link.Item2) { UseShellExecute = true }))).ToList();
        links.Add(Button(T("Abrir registro", "Open log"), (_, _) => { string log = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenControlEdge", "widget.log"); Process.Start(new ProcessStartInfo("explorer.exe", "/select," + log) { UseShellExecute = true }); }));
        p.Children.Add(new Border { Style = StyleOf("Oce.Separator") });
        Actions(p, links.ToArray()).Margin = new Thickness(0);
    }
    private void Updates()
    {
        var p = Inside(Card(T("Actualizaciones", "Updates")));
        var status = Muted(_release is null
            ? _noUpdateAvailable ? T("No hay actualizaciones disponibles", "No updates available") : T("Versión actual: ", "Current version: ") + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "2.1.0")
            : T("Actualización ", "Update ") + _release.Tag + T(" encontrada", " found"), new Thickness(0, 0, 0, 4));
        p.Children.Add(status);
        var notes = new TextBox { Style = StyleOf("Oce.Textarea"), Text = _release?.Notes ?? "", IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        if (_release is not null) p.Children.Add(notes);
        var install = Button(T("Descargar e instalar", "Download and install"), async (_, _) =>
        {
            if (_release is null) return;
            if (MessageBox.Show(T("Se verificará el SHA-256 y se reiniciará la aplicación para instalar la versión.", "SHA-256 will be verified and the app will restart to install this version."),
                T("Instalar actualización", "Install update"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            status.Text = T("Descargando y verificando…", "Downloading and verifying…");
            string? error = await UpdateInstaller.DownloadAndRestartAsync(_release);
            if (error is null) Application.Current.Shutdown();
            else status.Text = DisplayError(error);
        }, true);
        var check = Button(T("Buscar actualizaciones", "Check for updates"), async (_, _) =>
        {
            status.Text = T("Buscando…", "Checking…");
            UpdateCheckResult result = await UpdateService.CheckAsync();
            if (result.Error is not null) status.Text = DisplayError(result.Error);
            else if (result.Release is null) { _release = null; _noUpdateAvailable = true; RenderPage(); }
            else { _release = result.Release; _noUpdateAvailable = false; RenderPage(); }
        }, _release is null);
        if (_release is not null) Actions(p, install, check); else Actions(p, check);
        p.Children.Add(new Border { Style = StyleOf("Oce.Separator") });
        Settings settings = SettingsStore.Load();
        AddToggle(p, T("Buscar actualizaciones automáticamente (una vez al día)", "Check automatically (once a day)"), settings.AutoCheckUpdates,
            enabled => { var updated = SettingsStore.Update(x => x with { AutoCheckUpdates = enabled, LastAutoUpdateCheck = enabled ? null : x.LastAutoUpdateCheck }); if (updated is not null) _apply(updated); });
    }
    private void Feedback()
    {
        var p = Inside(Card(T("Cuéntanos", "Tell us"), T("Enviar abre un issue de GitHub para que lo revises. Hace falta una cuenta de GitHub.", "Send opens a GitHub issue for you to review. A GitHub account is required.")));
        var type = new ComboBox { ItemsSource = new[] { T("Error", "Bug"), T("Idea", "Idea"), T("Otro", "Other") }, SelectedIndex = 0, Width = 180,
            Style = StyleOf("Oce.Select"), ItemContainerStyle = StyleOf("Oce.Select.Item") };
        Row(p, T("Tipo", "Type"), type);
        p.Children.Add(FieldLabel(T("Mensaje", "Message")));
        var body = new TextBox { Style = StyleOf("Oce.Textarea"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; p.Children.Add(body);
        p.Children.Add(FieldLabel(T("Correo para responder (opcional)", "Reply email (optional)")));
        var email = new TextBox { Style = StyleOf("Oce.Input") }; p.Children.Add(email);
        string Payload() => $"Version: 2.1.0\nOS: {Environment.OSVersion.VersionString}\nScale: {SettingsStore.Load().UiScale?.ToString("0.00") ?? "auto"}\n\n{body.Text}\n\nReply email (optional): {email.Text}";
        Actions(p,
            Button(T("Enviar", "Send"), (_, _) => { string url = "https://github.com/danielfinchdev/open-control-edge/issues/new?title=" + Uri.EscapeDataString(type.Text + ": " + body.Text.Split('\n').FirstOrDefault()) + "&body=" + Uri.EscapeDataString(Payload()); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }, true),
            Button(T("Copiar al portapapeles", "Copy to clipboard"), (_, _) => Clipboard.SetText(Payload()), variant: "Secondary")).Margin = new Thickness(0, 14, 0, 0);
    }
    private static TextBlock FieldLabel(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };

    private static string DisplayError(string message) => message switch
    {
        "La dirección de releases no es segura." => T("La dirección de releases no es segura.", "The releases URL is not secure."),
        "La versión de GitHub no tiene un formato válido." => T("La versión de GitHub no tiene un formato válido.", "GitHub returned an invalid version."),
        "El release no contiene assets." => T("El release no contiene assets.", "The release has no assets."),
        "La URL del ZIP no es segura." => T("La URL del ZIP no es segura.", "The ZIP URL is not secure."),
        "El ZIP no tiene un digest SHA-256 verificable; no se instalará." => T("El ZIP no tiene un digest SHA-256 verificable; no se instalará.", "The ZIP has no verifiable SHA-256 digest; it will not be installed."),
        "El release no incluye un ZIP de instalación." => T("El release no incluye un ZIP de instalación.", "The release has no installation ZIP."),
        "No se pudo conectar con GitHub." => T("No se pudo conectar con GitHub.", "Could not connect to GitHub."),
        "La respuesta de releases no tiene el formato esperado." => T("La respuesta de releases no tiene el formato esperado.", "The release response has an unexpected format."),
        "No se pudo comprobar si hay actualizaciones." => T("No se pudo comprobar si hay actualizaciones.", "Could not check for updates."),
        "El digest SHA-256 del ZIP no coincide. No se instalará." => T("El digest SHA-256 del ZIP no coincide. No se instalará.", "The ZIP SHA-256 digest does not match. Nothing was installed."),
        "El ZIP contiene una ruta o enlace no seguro." => T("El ZIP contiene una ruta o enlace no seguro.", "The ZIP contains an unsafe path or link."),
        "Instala Open Control Edge antes de actualizar." => T("Instala Open Control Edge antes de actualizar.", "Install Open Control Edge before updating."),
        "La carpeta de instalación no está protegida; se cancela la actualización." => T("La carpeta de instalación no está protegida; se cancela la actualización.", "The install folder is not protected; the update was cancelled."),
        "El instalador terminó, pero PawnIO sigue sin estar disponible." => T("El instalador terminó, pero PawnIO sigue sin estar disponible.", "The installer finished, but PawnIO is still unavailable."),
        "No se pudo descargar o instalar PawnIO." => T("No se pudo descargar o instalar PawnIO.", "Could not download or install PawnIO."),
        _ => message,
    };

    internal static void SaveSnapshots(string directory)
    {
        // One agent in each state, so the Agents page shows every badge and button.
        static AgentStatus SampleStatus(AiProviderId id) => id switch
        {
            AiProviderId.Claude => new AgentStatus("connected", "Max", null, null),
            AiProviderId.Codex => new AgentStatus("connected", "Plus", null, null),
            AiProviderId.Cursor => new AgentStatus("session", null, AiDetector.CursorLoginMessage, null),
            AiProviderId.DeepSeek => new AgentStatus("error", null, "Clave API no válida", DateTimeOffset.Now.AddMinutes(-12)),
            AiProviderId.OpenRouter => new AgentStatus("connected", null, null, null),
            _ => new AgentStatus("missing", null, null, null),
        };
        foreach ((AppTheme theme, string themeName) in new[] { (AppTheme.Dark, "dark"), (AppTheme.Light, "light") })
        foreach ((UiLanguage language, string languageName) in new[] { (UiLanguage.Spanish, "es"), (UiLanguage.English, "en") })
        {
            ThemeManager.Apply(theme, RingColorTheme.Classic);
            Loc.Apply(language);
            var window = new SettingsWindow(static () => Task.CompletedTask, static _ => { }, SampleStatus, static _ => Task.CompletedTask)
                { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            window.Show();
            foreach (string category in Categories)
            {
                window._category = category; window.RenderPage(); window.UpdateLayout();
                string name = category.Replace(' ', '_').Replace('ó', 'o').Replace('í', 'i').ToLowerInvariant();
                Snapshot.SaveElement((FrameworkElement)window.Content, IOPath.Combine(directory, $"settings_{themeName}_{languageName}_{name}.png"));
            }
            if (theme == AppTheme.Dark && language == UiLanguage.Spanish)
            {
                window._category = "Agentes"; window._keyEditor = AiProviderId.DeepSeek; window.RenderPage(); window.UpdateLayout();
                Snapshot.SaveElement((FrameworkElement)window.Content, IOPath.Combine(directory, "settings_dark_es_agentes_api_key.png"));
                window._keyEditor = null;
            }
            window.Close();
        }
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic); Loc.Apply(UiLanguage.Spanish);
    }
}
