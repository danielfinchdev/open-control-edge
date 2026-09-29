using System.Diagnostics;
using System.Collections.Frozen;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;

namespace OpenControlEdge.Views;

/// A lightweight, on-demand settings window. Its controls are created only while it is open.
internal sealed class SettingsWindow : Window
{
    private readonly Action _refresh;
    private readonly Action<Settings> _apply;
    private readonly StackPanel _content = new();
    private readonly StackPanel _navigation = new();
    private string _category = "General";
    private static readonly string[] Categories = ["General", "Personalización", "Agentes", "Información", "Actualizaciones", "Feedback"];

    internal SettingsWindow(Action refresh, Action<Settings> apply)
    {
        _refresh = refresh;
        _apply = apply;
        Width = 790; Height = 590; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        var frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            BorderBrush = Brush(ThemeManager.Border), Background = Brush(ThemeManager.Background),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = .32 } };
        var root = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(22, 16, 16, 12) };
        var close = new Button { Content = "×", Width = 32, Height = 30, FontSize = 20, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brush(ThemeManager.Text) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var title = new TextBlock { Text = "Open Control Edge · " + T("Ajustes", "Settings"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush(ThemeManager.Text), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title); header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _navigation.Width = 184; _navigation.Margin = new Thickness(12, 0, 8, 14);
        foreach (string category in Categories) { var b = new Button { Tag = category, Content = "◆   " + Category(category), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10, 8, 10), Margin = new Thickness(0, 2, 0, 2), BorderThickness = new Thickness(0), Background = category == _category ? Brush(ThemeManager.Raised) : Brushes.Transparent, Foreground = Brush(ThemeManager.Text) }; b.Click += (_, _) => { _category = category; RenderPage(); }; _navigation.Children.Add(b); }
        DockPanel.SetDock(_navigation, Dock.Left); root.Children.Add(_navigation);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 18, 16), Content = _content };
        root.Children.Add(scroll); frame.Child = root; Content = frame;
        ThemeManager.Changed += OnThemeChanged; Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        Loc.Changed += OnLanguageChanged; Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        RenderPage();
    }

    private void OnThemeChanged() { Dispatcher.InvokeAsync(() => { Background = Brushes.Transparent; RenderPage(); }); }
    private void OnLanguageChanged() => Dispatcher.InvokeAsync(RenderPage);
    private static string T(string es, string en) => Loc.Language == UiLanguage.English ? en : es;
    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Brush Accent => new SolidColorBrush(Color.FromRgb(99, 102, 241));
    private string Category(string c) => c switch { "Personalización" => T(c, "Appearance"), "Agentes" => T(c, "Agents"), "Información" => T(c, "About"), "Actualizaciones" => T(c, "Updates"), "Feedback" => c, _ => T(c, "General") };
    private void RenderPage()
    {
        foreach (Button button in _navigation.Children)
        {
            button.Background = Equals(button.Tag, _category) ? Brush(ThemeManager.Raised) : Brushes.Transparent;
            button.Foreground = Brush(ThemeManager.Text);
            button.Content = "◆   " + Category((string)button.Tag);
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
        var card = new Border { Background = Brush(ThemeManager.Surface), BorderBrush = Brush(ThemeManager.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 12), Child = panel }; _content.Children.Add(card); return card;
    }
    private static StackPanel Inside(Border card) => (StackPanel)card.Child;
    private static Button Button(string text, RoutedEventHandler click, bool primary = false)
    { var b = new Button { Content = text, Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 5, 8, 0), Background = primary ? Accent : Brush(ThemeManager.Button), Foreground = primary ? Brushes.White : Brush(ThemeManager.Text), BorderBrush = Brush(ThemeManager.Border), BorderThickness = new Thickness(1), Cursor = Cursors.Hand }; b.Click += click; return b; }
    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brush(ThemeManager.Text), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 10, 5) };
    private static void Store(Func<Settings, Settings> update) => SettingsStore.Update(update);
    private void General()
    {
        Settings s = SettingsStore.Load(); var card = Card(T("Preferencias", "Preferences")); var p = Inside(card);
        var startup = new CheckBox { Content = T("Iniciar con Windows", "Start with Windows"), IsChecked = AutoStartService.IsEnabled(), Foreground = Brush(ThemeManager.Text), Margin = new Thickness(0, 5, 0, 10) };
        startup.Checked += (_, _) => { string? e = AutoStartService.Enable(); if (e != null) MessageBox.Show(e); }; startup.Unchecked += (_, _) => { string? e = AutoStartService.Disable(); if (e != null) MessageBox.Show(e); }; p.Children.Add(startup);
        AddChoice(p, T("Modo del panel", "Panel mode"), ["Fijado", "Automático"], s.PanelMode == PanelMode.Auto ? 1 : 0, i => Store(x => x with { PanelMode = i == 0 ? PanelMode.Pinned : PanelMode.Auto }));
        AddChoice(p, T("Intervalo de actualización", "Usage refresh interval"), ["2 min", "5 min", "10 min", "15 min"], 0, _ => { });
        var last = Label(T("Última actualización: ", "Last updated: ") + "—"); p.Children.Add(last); p.Children.Add(Button(T("Refrescar ahora", "Refresh now"), (_, _) => { _refresh(); last.Text = T("Actualizando…", "Refreshing…"); }, true));
        p.Children.Add(Button(T("Desinstalar…", "Uninstall…"), (_, _) => { if (MessageBox.Show(T("¿Desinstalar Open Control Edge?", "Uninstall Open Control Edge?"), "Open Control Edge", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { string? error = AutoStartService.Uninstall(); MessageBox.Show(error ?? T("Se desinstalará al reiniciar.", "It will be uninstalled after restart.")); } }));
        var extra = Card(T("Opciones", "Options")); var ep = Inside(extra);
        AddToggle(ep, T("Renovar la sesión de Claude automáticamente", "Automatically renew Claude session"), s.AutoRenewClaude, v => Store(x => x with { AutoRenewClaude = v }));
        AddToggle(ep, T("Permitir liberar RAM con un clic", "Allow one-click RAM cleanup"), s.RamCleanup, v => Store(x => x with { RamCleanup = v }));
    }
    private void Appearance()
    {
        Settings s = SettingsStore.Load(); var p = Inside(Card(T("Aspecto", "Appearance")));
        AddChoice(p, T("Idioma", "Language"), ["Español", "English"], s.Language == UiLanguage.English ? 1 : 0, i => { var n = SettingsStore.Update(x => x with { Language = i == 0 ? UiLanguage.Spanish : UiLanguage.English }); if (n != null) Loc.Apply(n.Language); });
        AddChoice(p, T("Tema", "Theme"), ["Oscuro", "Claro", "Sistema"], (int)s.Theme, i => { var n = SettingsStore.Update(x => x with { Theme = (AppTheme)i }); if (n != null) ThemeManager.Apply(n.Theme, n.ColorTheme); });
        AddChoice(p, T("Color de anillos", "Ring colors"), ["Classic", "Mono", "Ocean", "Sunset", "Neon"], (int)s.ColorTheme, i => { var n = SettingsStore.Update(x => x with { ColorTheme = (RingColorTheme)i }); if (n != null) ThemeManager.Apply(n.Theme, n.ColorTheme); });
        AddChoice(p, T("Tamaño", "Widget size"), [T("Automático", "Automatic"), "80%", "90%", "100%", "110%", "120%"], s.UiScale is null ? 0 : Math.Clamp((int)Math.Round(s.UiScale.Value * 10) - 7, 1, 5), i => { var n = SettingsStore.Update(x => x with { UiScale = i == 0 ? null : .7 + i * .1 }); if (n != null) _apply(n); });
    }
    private void AddChoice(Panel panel, string label, string[] choices, int selected, Action<int> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) }; row.Children.Add(Label(label));
        var combo = new ComboBox { Width = 190, ItemsSource = choices, SelectedIndex = selected, Background = Brush(ThemeManager.Button), Foreground = Brush(ThemeManager.Text), Padding = new Thickness(5) }; combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); }; row.Children.Add(combo); panel.Children.Add(row);
    }
    private void AddToggle(Panel panel, string text, bool value, Action<bool> changed)
    { var c = new CheckBox { Content = text, IsChecked = value, Foreground = Brush(ThemeManager.Text), Margin = new Thickness(0, 5, 0, 5) }; c.Checked += (_, _) => changed(true); c.Unchecked += (_, _) => changed(false); panel.Children.Add(c); }
    private void Agents()
    {
        foreach (AiProviderId id in AiDetector.All)
        {
            string name = id.ToString(); bool installed = AiDetector.IsInstalled(id); var card = Card("●  " + name, installed ? T("Instalado o configurado", "Installed or configured") : T("No instalado", "Not installed")); var p = Inside(card);
            Settings settings = SettingsStore.Load(); bool visible = !settings.Providers.TryGetValue(name, out ProviderVisibility v) || v != ProviderVisibility.Hide;
            AddToggle(p, T("Mostrar en el panel", "Show on panel"), visible, show => Store(x => { var map = x.Providers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase); map[name] = show ? ProviderVisibility.Show : ProviderVisibility.Hide; return x with { Providers = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) }; }));
            if (id is AiProviderId.DeepSeek or AiProviderId.OpenRouter)
            {
                var box = new PasswordBox { Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(8), Background = Brush(ThemeManager.Button), Foreground = Brush(ThemeManager.Text) }; p.Children.Add(box);
                p.Children.Add(Button(T("Guardar", "Save"), (_, _) => { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(box.Password); bool ok = ProviderKeyStore.Save(name.ToLowerInvariant(), bytes); System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); MessageBox.Show(ok ? T("Clave guardada", "Key saved") : T("No se pudo guardar", "Could not save")); }));
                p.Children.Add(Button(T("Borrar clave", "Delete key"), (_, _) => ProviderKeyStore.Delete(name.ToLowerInvariant())));
            }
            else if (id is AiProviderId.Claude or AiProviderId.Codex or AiProviderId.Cursor)
                p.Children.Add(Button(T("Conectar", "Connect"), (_, _) => { string cli = id switch { AiProviderId.Claude => "claude", AiProviderId.Codex => "codex", _ => "cursor-agent" }; UnelevatedLauncher.Run(cli, "login", Environment.CurrentDirectory, hidden: false, wait: null); }));
        }
    }
    private void About()
    {
        var p = Inside(Card("Open Control Edge", "2.1.0"));
        bool pawn = false; try { pawn = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; } catch { }
        p.Children.Add(Label((pawn ? "🟢 " : "🔴 ") + "PawnIO: " + (pawn ? T("instalado", "installed") : T("no instalado", "missing"))));
        if (!pawn) p.Children.Add(Button(T("Descargar e instalar", "Download and install"), async (sender, _) => { var b = (Button)sender; b.IsEnabled = false; b.Content = T("Verificando e instalando…", "Verifying and installing…"); string? error = await PawnIoInstaller.InstallAsync(); MessageBox.Show(error ?? T("PawnIO instalado.", "PawnIO installed.")); RenderPage(); }));
        p.Children.Add(Label(T("Lecturas de sensores: ", "Sensor readings: ") + T("consulta el panel para ver CPU/GPU", "see CPU/GPU readings on the panel")));
        foreach (var link in new[] { ("Repositorio", "https://github.com/danielfinchdev/open-control-edge"), ("Licencia", "https://github.com/danielfinchdev/open-control-edge/blob/v2.1/LICENSE"), ("Licencias de terceros", "https://github.com/danielfinchdev/open-control-edge/blob/v2.1/THIRD-PARTY-NOTICES.txt") }) p.Children.Add(Button(link.Item1, (_, _) => Process.Start(new ProcessStartInfo(link.Item2) { UseShellExecute = true })));
        p.Children.Add(Button(T("Abrir registro", "Open log"), (_, _) => { string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenControlEdge", "widget.log"); Process.Start(new ProcessStartInfo("explorer.exe", "/select," + log) { UseShellExecute = true }); }));
    }
    private void Updates()
    {
        var p = Inside(Card(T("Actualizaciones", "Updates"))); var status = Label(""); p.Children.Add(status);
        p.Children.Add(Button(T("Buscar actualizaciones", "Check for updates"), async (_, _) => { status.Text = T("Buscando…", "Checking…"); try { using var http = new System.Net.Http.HttpClient(); http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenControlEdge"); var json = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/danielfinchdev/open-control-edge/releases/latest")); string tag = json.RootElement.GetProperty("tag_name").GetString() ?? ""; status.Text = Version.TryParse(tag.TrimStart('v'), out var v) && v > new Version(2, 1, 0) ? T("Nueva versión: ", "New version: ") + tag : T("No hay actualizaciones disponibles", "No updates available"); } catch { status.Text = T("No se pudo comprobar", "Could not check for updates"); } }));
        AddToggle(p, T("Buscar actualizaciones automáticamente (una vez al día)", "Check automatically (once a day)"), false, _ => { });
    }
    private void Feedback()
    {
        var p = Inside(Card(T("Cuéntanos", "Tell us"), T("Enviar abre un issue de GitHub para que lo revises. Hace falta una cuenta de GitHub.", "Send opens a GitHub issue for you to review. A GitHub account is required.")));
        var type = new ComboBox { ItemsSource = new[] { T("Error", "Bug"), T("Idea", "Idea"), T("Otro", "Other") }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8) }; p.Children.Add(type);
        var body = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, Padding = new Thickness(8), Background = Brush(ThemeManager.Button), Foreground = Brush(ThemeManager.Text), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; p.Children.Add(body);
        var email = new TextBox { Text = T("Correo opcional para responder", "Optional reply email"), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8), Background = Brush(ThemeManager.Button), Foreground = Brush(ThemeManager.Text) }; p.Children.Add(email);
        string Payload() => $"Version: 2.1.0\nOS: {Environment.OSVersion.VersionString}\nScale: {SettingsStore.Load().UiScale?.ToString("0.00") ?? "auto"}\n\n{body.Text}\n\nReply email (optional): {email.Text}";
        p.Children.Add(Button(T("Copiar al portapapeles", "Copy to clipboard"), (_, _) => Clipboard.SetText(Payload())));
        p.Children.Add(Button(T("Enviar", "Send"), (_, _) => { string url = "https://github.com/danielfinchdev/open-control-edge/issues/new?title=" + Uri.EscapeDataString(type.Text + ": " + body.Text.Split('\n').FirstOrDefault()) + "&body=" + Uri.EscapeDataString(Payload()); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }, true));
    }

    internal static void SaveSnapshots(string directory)
    {
        foreach ((AppTheme theme, string themeName) in new[] { (AppTheme.Dark, "dark"), (AppTheme.Light, "light") })
        foreach ((UiLanguage language, string languageName) in new[] { (UiLanguage.Spanish, "es"), (UiLanguage.English, "en") })
        {
            ThemeManager.Apply(theme, RingColorTheme.Classic);
            Loc.Apply(language);
            var window = new SettingsWindow(static () => { }, static _ => { }) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            window.Show();
            foreach (string category in Categories)
            {
                window._category = category; window.RenderPage(); window.UpdateLayout();
                string name = category.Replace(' ', '_').Replace('ó', 'o').Replace('í', 'i').ToLowerInvariant();
                Snapshot.SaveElement((FrameworkElement)window.Content, Path.Combine(directory, $"settings_{themeName}_{languageName}_{name}.png"));
            }
            window.Close();
        }
        ThemeManager.Apply(AppTheme.Dark, RingColorTheme.Classic); Loc.Apply(UiLanguage.Spanish);
    }
}
