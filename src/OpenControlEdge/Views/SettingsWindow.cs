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
using Microsoft.Win32;
using static OpenControlEdge.Interop.NativeMethods;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;
using IOPath = System.IO.Path;

namespace OpenControlEdge.Views;

/// A lightweight, on-demand settings window. Its controls are created only while it is open.
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
    private string _category = "General";
    private UpdateRelease? _release;
    private bool _noUpdateAvailable;
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
        Width = 790; Height = 590; WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        var frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            BorderBrush = Brush(ThemeManager.Border), Background = Brush(ThemeManager.Background),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = .32 } };
        var root = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(22, 16, 16, 12) };
        var close = new Button { Content = "×", Width = 32, Height = 30, FontSize = 20, Style = (Style)Application.Current.FindResource("Oce.Button.Ghost"), Foreground = Brush(ThemeManager.Text) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var title = new TextBlock { Text = "Open Control Edge · " + T("Ajustes", "Settings"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush(ThemeManager.Text), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title); header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _navigation.Width = 184; _navigation.Margin = new Thickness(12, 0, 8, 14);
        foreach (string category in Categories) { var b = new Button { Tag = category, Content = CategoryIcon(category) + "   " + Category(category), Style = (Style)Application.Current.FindResource("Oce.Button.Ghost"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10, 8, 10), Margin = new Thickness(0, 2, 0, 2), Background = category == _category ? Brush(ThemeManager.Raised) : Brushes.Transparent, Foreground = Brush(ThemeManager.Text) }; b.Click += (_, _) => { _category = category; RenderPage(); }; _navigation.Children.Add(b); }
        DockPanel.SetDock(_navigation, Dock.Left); root.Children.Add(_navigation);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 18, 16), Content = _content };
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

    private void OnThemeChanged() { Dispatcher.InvokeAsync(() => { Background = Brushes.Transparent; RenderPage(); }); }
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
    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Brush Accent => new SolidColorBrush(Color.FromRgb(99, 102, 241));
    private string Category(string c) => c switch { "Personalización" => T(c, "Appearance"), "Agentes" => T(c, "Agents"), "Información" => T(c, "About"), "Actualizaciones" => T(c, "Updates"), "Feedback" => c, _ => T(c, "General") };
    private static string CategoryIcon(string c) => c switch { "General" => "⚙", "Personalización" => "◉", "Agentes" => "◎", "Información" => "ⓘ", "Actualizaciones" => "↻", _ => "✉" };
    private void RenderPage()
    {
        foreach (Button button in _navigation.Children)
        {
            button.Background = Equals(button.Tag, _category) ? Brush(ThemeManager.Raised) : Brushes.Transparent;
            button.Foreground = Brush(ThemeManager.Text);
            button.Content = CategoryIcon((string)button.Tag) + "   " + Category((string)button.Tag);
        }
        _content.Children.Clear();
        AddHeading(Category(_category));
        switch (_category) { case "General": General(); break; case "Personalización": Appearance(); break; case "Agentes": Agents(); break; case "Información": About(); break; case "Actualizaciones": Updates(); break; case "Feedback": Feedback(); break; }
    }
    private void AddHeading(string text) => _content.Children.Add(new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brush(ThemeManager.Text), Margin = new Thickness(0, 4, 0, 16) });
    private Border Card(string title, string description = "")
    {
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Brush(ThemeManager.Text) });
        if (description.Length > 0) panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Brush(ThemeManager.TextSecondary), Margin = new Thickness(0, 4, 0, 10) });
        var card = new Border { Style = (Style)Application.Current.FindResource("Oce.Card"), Child = panel }; _content.Children.Add(card); return card;
    }
    private static StackPanel Inside(Border card) => (StackPanel)card.Child;
    private static Button Button(string text, RoutedEventHandler click, bool primary = false, string? variant = null)
    {
        string styleKey = variant is not null ? "Oce.Button." + variant
            : text.Contains("Desinstalar", StringComparison.OrdinalIgnoreCase) || text.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
                ? "Oce.Button.Destructive" : primary ? "Oce.Button.Primary" : "Oce.Button.Outline";
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(styleKey) };
        b.Click += click; return b;
    }
    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brush(ThemeManager.Text), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 10, 5) };
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
        var last = Label(T("Última actualización: ", "Last updated: ") + (lastValue?.ToLocalTime().ToString("t", Loc.Culture) ?? "—")); p.Children.Add(last);
        p.Children.Add(Button(T("Refrescar ahora", "Refresh now"), async (_, _) =>
        {
            last.Text = T("Actualizando…", "Refreshing…");
            await _refresh();
            last.Text = T("Última actualización: ", "Last updated: ") + (_lastRefresh()?.ToLocalTime().ToString("t", Loc.Culture) ?? DateTime.Now.ToString("t", Loc.Culture));
        }, true));
        p.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Oce.Separator") });
        p.Children.Add(Button(T("Desinstalar…", "Uninstall…"), (_, _) => { if (MessageBox.Show(T("¿Desinstalar Open Control Edge?", "Uninstall Open Control Edge?"), "Open Control Edge", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { string? error = AutoStartService.Uninstall(); MessageBox.Show(error ?? T("Se desinstalará al reiniciar.", "It will be uninstalled after restart.")); } }));
        var extra = Card(T("Opciones", "Options")); var ep = Inside(extra);
        AddToggle(ep, T("Renovar la sesión de Claude automáticamente", "Automatically renew Claude session"), s.AutoRenewClaude, v => Store(x => x with { AutoRenewClaude = v }));
        AddToggle(ep, T("Permitir liberar RAM con un clic", "Allow one-click RAM cleanup"), s.RamCleanup, v => Store(x => x with { RamCleanup = v }));
        ep.Children.Add(Button(T("Restablecer ajustes", "Reset settings"), (_, _) =>
        {
            if (MessageBox.Show(T("¿Restablecer todos los ajustes?", "Reset all settings?"), "Open Control Edge",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Settings? reset = SettingsStore.Update(_ => Settings.Defaults);
            if (reset is null) return;
            ThemeManager.Apply(reset.Theme, reset.ColorTheme); Loc.Apply(reset.Language); _apply(reset); RenderPage();
        }, variant: "Secondary"));
    }
    private void Appearance()
    {
        Settings s = SettingsStore.Load(); var p = Inside(Card(T("Aspecto", "Appearance")));
        AddChoice(p, T("Idioma", "Language"), ["Español", "English"], s.Language == UiLanguage.English ? 1 : 0, i => { var n = SettingsStore.Update(x => x with { Language = i == 0 ? UiLanguage.Spanish : UiLanguage.English }); if (n != null) Loc.Apply(n.Language); });
        AddChoice(p, T("Tema", "Theme"), ["Oscuro", "Claro", "Sistema"], (int)s.Theme, i => { var n = SettingsStore.Update(x => x with { Theme = (AppTheme)i }); if (n != null) ThemeManager.Apply(n.Theme, n.ColorTheme); });
        AddRingThemes(s);
        AddScale(s);
    }
    private void AddChoice(Panel panel, string label, string[] choices, int selected, Action<int> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) }; row.Children.Add(Label(label));
        var combo = new ComboBox { Width = 190, ItemsSource = choices, SelectedIndex = selected, Style = (Style)Application.Current.FindResource("Oce.Select"),
            ItemContainerStyle = (Style)Application.Current.FindResource("Oce.Select.Item") };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); }; row.Children.Add(combo); panel.Children.Add(row);
    }
    private void AddToggle(Panel panel, string text, bool value, Action<bool> changed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        var toggle = new ToggleButton { Style = (Style)Application.Current.FindResource("Oce.Switch"), IsChecked = value, Margin = new Thickness(10, 2, 0, 0) };
        toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false);
        DockPanel.SetDock(toggle, Dock.Right); row.Children.Add(toggle); row.Children.Add(Label(text)); panel.Children.Add(row);
    }
    private void AddRingThemes(Settings s)
    {
        var card = Card(T("Color de los anillos", "Ring colors"), T("Previsualización", "Preview"));
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
            foreach (Color color in swatches[i]) rings.Children.Add(new Ellipse { Width = 18, Height = 18, Margin = new Thickness(3), StrokeThickness = 3, Stroke = new SolidColorBrush(color), Fill = Brushes.Transparent });
            var contents = new StackPanel(); contents.Children.Add(rings); contents.Children.Add(new TextBlock { Text = names[i], Foreground = Brush(ThemeManager.Text), HorizontalAlignment = HorizontalAlignment.Center });
            var sample = new ToggleButton { Content = contents, IsChecked = (int)s.ColorTheme == i, Padding = new Thickness(7), Margin = new Thickness(3),
                Style = (Style)Application.Current.FindResource("Oce.SampleChoice") };
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
        var slider = new Slider { Style = (Style)Application.Current.FindResource("Oce.ScaleSlider"), Value = s.UiScale ?? 1, IsEnabled = s.UiScale is not null, Margin = new Thickness(2, 8, 2, 0) };
        var value = Label(s.UiScale is null ? T("Escala automática", "Automatic scale") : T("Escala manual: ", "Manual scale: ") + $"{s.UiScale:P0}");
        var auto = new ToggleButton { Style = (Style)Application.Current.FindResource("Oce.Switch"), IsChecked = s.UiScale is null };
        var row = new DockPanel(); DockPanel.SetDock(auto, Dock.Right); row.Children.Add(auto); row.Children.Add(Label(T("Automático", "Automatic")));
        Inside(card).Children.Add(row); Inside(card).Children.Add(value); Inside(card).Children.Add(slider);
        auto.Checked += (_, _) => { slider.IsEnabled = false; value.Text = T("Escala automática", "Automatic scale"); var updated = SettingsStore.Update(x => x with { UiScale = null }); if (updated != null) _apply(updated); };
        auto.Unchecked += (_, _) => { slider.IsEnabled = true; var updated = SettingsStore.Update(x => x with { UiScale = slider.Value }); if (updated != null) _apply(updated); };
        slider.ValueChanged += (_, _) => { value.Text = T("Escala manual: ", "Manual scale: ") + $"{slider.Value:P0}"; if (slider.IsEnabled) { var updated = SettingsStore.Update(x => x with { UiScale = slider.Value }); if (updated != null) _apply(updated); } };
    }
    private void Agents()
    {
        foreach (AiProviderId id in AiDetector.All)
        {
            string name = id.ToString();
            AgentStatus status = _agentStatus(id);
            var card = Card(string.Empty); var p = Inside(card);
            var heading = new DockPanel { LastChildFill = true };
            var logo = new Border { Style = (Style)Application.Current.FindResource("Oce.Badge"), Margin = new Thickness(0, 0, 9, 0),
                Child = new TextBlock { Text = name[..Math.Min(2, name.Length)].ToUpperInvariant(), Foreground = Brush(ThemeManager.Text), FontWeight = FontWeights.SemiBold } };
            DockPanel.SetDock(logo, Dock.Left); heading.Children.Add(logo);
            var title = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush(ThemeManager.Text), VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(title);
            var stateText = status.Kind switch
            {
                "connected" => "● " + T("Conectado", "Connected") + (string.IsNullOrWhiteSpace(status.Plan) ? "" : " · " + status.Plan),
                "missing" => "○ " + T("No instalado", "Not installed"),
                "session" => "⚠ " + T("Sin sesión", "No session"),
                _ => "✖ " + T("Error de sincronización", "Sync error"),
            };
            var state = Label(stateText); state.Margin = new Thickness(0, 7, 0, 2);
            if (status.Kind == "error") state.Foreground = Brush(ThemeManager.TextSecondary);
            p.Children.Add(heading); p.Children.Add(state);
            if (status.Message is not null && status.Kind is "error" or "session")
            {
                var detail = Label((Loc.Message(status.Message) ?? status.Message) +
                    (status.FailedAt is DateTimeOffset failedAt ? " · " + failedAt.ToLocalTime().ToString("g", Loc.Culture) : ""));
                detail.TextWrapping = TextWrapping.Wrap;
                if (id == AiProviderId.Claude && status.Kind == "error") detail.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                p.Children.Add(detail);
            }
            Settings settings = SettingsStore.Load(); bool visible = !settings.Providers.TryGetValue(name, out ProviderVisibility v) || v != ProviderVisibility.Hide;
            AddToggle(p, T("Mostrar", "Show"), visible, show =>
            {
                var updated = SettingsStore.Update(x => { var map = x.Providers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase); map[name] = show ? ProviderVisibility.Show : ProviderVisibility.Hide; return x with { Providers = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) }; });
                if (updated != null) _apply(updated);
            });
            if (id is AiProviderId.DeepSeek or AiProviderId.OpenRouter)
            {
                var box = new PasswordBox { Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(8), Background = Brush(ThemeManager.Button), Foreground = Brush(ThemeManager.Text) }; p.Children.Add(box);
                p.Children.Add(Button(T("Guardar", "Save"), async (_, _) =>
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(box.Password);
                    bool ok = ProviderKeyStore.Save(name.ToLowerInvariant(), bytes);
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                    MessageBox.Show(ok ? T("Clave guardada", "Key saved") : T("No se pudo guardar", "Could not save"));
                    if (ok) { await _retry(id); RenderPage(); }
                }, true));
                p.Children.Add(Button(T("Borrar", "Delete"), async (_, _) => { ProviderKeyStore.Delete(name.ToLowerInvariant()); await _retry(id); RenderPage(); }));
            }
            else if (id is AiProviderId.Claude or AiProviderId.Codex or AiProviderId.Cursor)
            {
                p.Children.Add(Button(T("Conectar", "Connect"), (_, _) =>
                {
                    string cli = id switch { AiProviderId.Claude => "claude", AiProviderId.Codex => "codex", _ => "cursor-agent" };
                    var result = UnelevatedLauncher.Run(cli, "login", Environment.CurrentDirectory, hidden: false, wait: null);
                    if (!result.Started) MessageBox.Show(DisplayError(result.Error ?? T("No se pudo iniciar el login.", "Could not start login.")));
                }, true));
            }
            if (status.Kind == "error" || status.Kind == "session")
                p.Children.Add(Button(T("Reintentar", "Retry"), async (_, _) => { await _retry(id); RenderPage(); }));
        }
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
        var pawnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        pawnRow.Children.Add(new Ellipse { Style = (Style)Application.Current.FindResource(pawnRunning ? "Oce.Led.On" : "Oce.Led"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 9, 0) });
        pawnRow.Children.Add(Label("PawnIO: " + (pawnRunning ? T("instalado y en marcha", "installed and running") : pawnInstalled ? T("instalado, servicio detenido", "installed, service stopped") : T("falta", "missing"))));
        p.Children.Add(pawnRow);
        if (!pawnInstalled) p.Children.Add(Button(T("Descargar e instalar", "Download and install"), async (sender, _) => { var b = (Button)sender; b.IsEnabled = false; b.Content = T("Verificando e instalando…", "Verifying and installing…"); string? error = await PawnIoInstaller.InstallAsync(); MessageBox.Show(error is null ? T("PawnIO instalado.", "PawnIO installed.") : DisplayError(error)); RenderPage(); }));
        bool sensors = _sensorsAvailable();
        p.Children.Add(Label(T("Lecturas de temperatura disponibles: ", "Temperature readings available: ") + T(sensors ? "Sí" : "No", sensors ? "Yes" : "No")));
        foreach (var link in new[] { (T("Repositorio", "Repository"), "https://github.com/danielfinchdev/open-control-edge"), (T("Licencia", "License"), "https://github.com/danielfinchdev/open-control-edge/blob/v2.1/LICENSE"), (T("Licencias de terceros", "Third-party licenses"), "https://github.com/danielfinchdev/open-control-edge/blob/v2.1/THIRD-PARTY-NOTICES.txt") }) p.Children.Add(Button(link.Item1, (_, _) => Process.Start(new ProcessStartInfo(link.Item2) { UseShellExecute = true })));
        p.Children.Add(Button(T("Abrir registro", "Open log"), (_, _) => { string log = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenControlEdge", "widget.log"); Process.Start(new ProcessStartInfo("explorer.exe", "/select," + log) { UseShellExecute = true }); }));
    }
    private void Updates()
    {
        var p = Inside(Card(T("Actualizaciones", "Updates")));
        var status = Label(_release is null
            ? _noUpdateAvailable ? T("No hay actualizaciones disponibles", "No updates available") : ""
            : T("Actualización ", "Update ") + _release.Tag + T(" encontrada", " found"));
        status.TextWrapping = TextWrapping.Wrap; p.Children.Add(status);
        var notes = new TextBox { Style = (Style)Application.Current.FindResource("Oce.Textarea"), Text = _release?.Notes ?? "", IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
        if (_release is not null) p.Children.Add(install);
        p.Children.Add(Button(T("Buscar actualizaciones", "Check for updates"), async (_, _) =>
        {
            status.Text = T("Buscando…", "Checking…");
            UpdateCheckResult result = await UpdateService.CheckAsync();
            if (result.Error is not null) status.Text = DisplayError(result.Error);
            else if (result.Release is null) { _release = null; _noUpdateAvailable = true; RenderPage(); }
            else { _release = result.Release; _noUpdateAvailable = false; RenderPage(); }
        }));
        Settings settings = SettingsStore.Load();
        AddToggle(p, T("Buscar actualizaciones automáticamente (una vez al día)", "Check automatically (once a day)"), settings.AutoCheckUpdates,
            enabled => { var updated = SettingsStore.Update(x => x with { AutoCheckUpdates = enabled, LastAutoUpdateCheck = enabled ? null : x.LastAutoUpdateCheck }); if (updated is not null) _apply(updated); });
    }
    private void Feedback()
    {
        var p = Inside(Card(T("Cuéntanos", "Tell us"), T("Enviar abre un issue de GitHub para que lo revises. Hace falta una cuenta de GitHub.", "Send opens a GitHub issue for you to review. A GitHub account is required.")));
        var type = new ComboBox { ItemsSource = new[] { T("Error", "Bug"), T("Idea", "Idea"), T("Otro", "Other") }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8) }; p.Children.Add(type);
        var body = new TextBox { Style = (Style)Application.Current.FindResource("Oce.Textarea"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; p.Children.Add(body);
        var email = new TextBox { Text = T("Correo opcional para responder", "Optional reply email"), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8), Background = Brush(ThemeManager.Button), Foreground = Brush(ThemeManager.Text) }; p.Children.Add(email);
        string Payload() => $"Version: 2.1.0\nOS: {Environment.OSVersion.VersionString}\nScale: {SettingsStore.Load().UiScale?.ToString("0.00") ?? "auto"}\n\n{body.Text}\n\nReply email (optional): {email.Text}";
        p.Children.Add(Button(T("Copiar al portapapeles", "Copy to clipboard"), (_, _) => Clipboard.SetText(Payload()), variant: "Secondary"));
        p.Children.Add(Button(T("Enviar", "Send"), (_, _) => { string url = "https://github.com/danielfinchdev/open-control-edge/issues/new?title=" + Uri.EscapeDataString(type.Text + ": " + body.Text.Split('\n').FirstOrDefault()) + "&body=" + Uri.EscapeDataString(Payload()); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }, true));
    }

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
        foreach ((AppTheme theme, string themeName) in new[] { (AppTheme.Dark, "dark"), (AppTheme.Light, "light") })
        foreach ((UiLanguage language, string languageName) in new[] { (UiLanguage.Spanish, "es"), (UiLanguage.English, "en") })
        {
            ThemeManager.Apply(theme, RingColorTheme.Classic);
            Loc.Apply(language);
            var window = new SettingsWindow(static () => Task.CompletedTask, static _ => { },
                static _ => new AgentStatus("missing", null, null, null), static _ => Task.CompletedTask)
                { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            window.Show();
            foreach (string category in Categories)
            {
                window._category = category; window.RenderPage(); window.UpdateLayout();
                string name = category.Replace(' ', '_').Replace('ó', 'o').Replace('í', 'i').ToLowerInvariant();
                Snapshot.SaveElement((FrameworkElement)window.Content, IOPath.Combine(directory, $"settings_{themeName}_{languageName}_{name}.png"));
            }
            window.Close();
        }
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic); Loc.Apply(UiLanguage.Spanish);
    }
}
