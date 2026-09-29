using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenControlEdge.Services;

namespace OpenControlEdge.Ui;

/// Colour tokens of the interface, published as application resources ("Theme.*") so every view picks them up
/// through DynamicResource and repaints in place when the theme changes. Code that paints from Palette (rings,
/// bars) listens to Changed and re-applies its last readings.
internal static class ThemeManager
{
    public const string Background = "Theme.Background";   // panel and card
    public const string Surface = "Theme.Surface";         // tab list, plan badge, text boxes
    public const string Raised = "Theme.Raised";           // selected tab
    public const string Border = "Theme.Border";
    public const string Text = "Theme.Text";               // text and monochrome logos
    public const string TextSecondary = "Theme.TextSecondary";
    public const string RingTrack = "Theme.RingTrack";     // ring and bar track
    public const string Button = "Theme.Button";           // round panel buttons, at rest
    public const string ButtonHover = "Theme.ButtonHover";
    public const string ButtonPressed = "Theme.ButtonPressed";
    public const string Menu = "Theme.Menu";               // tray menu and dialogs

    private static bool _listening;

    public static AppTheme Theme { get; private set; } = AppTheme.Dark;
    public static RingColorTheme ColorTheme { get; private set; } = RingColorTheme.Classic;

    /// True while the light tokens are in use (Light, or System with Windows in light mode).
    public static bool IsLight { get; private set; }

    /// Raised on the UI thread after the tokens and Palette have changed.
    public static event Action? Changed;

    public static void Apply(AppTheme theme, RingColorTheme colors)
    {
        Theme = theme;
        ColorTheme = colors;
        if (theme == AppTheme.System && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        else if (theme != AppTheme.System && _listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listening = false;
        }
        Refresh();
    }

    /// Detaches from SystemEvents (a static event that would otherwise outlive the application).
    public static void Shutdown()
    {
        if (!_listening) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listening = false;
    }

    private static void Refresh()
    {
        if (Application.Current is not Application app) return;
        IsLight = Theme == AppTheme.Light || (Theme == AppTheme.System && WindowsUsesLightTheme());

        ResourceDictionary r = app.Resources;
        if (IsLight)
        {
            r[Background] = Make(0xFFF4F4F6);
            r[Surface] = Make(0xFFE6E6EA);
            r[Raised] = Make(0xFFFFFFFF);
            r[Border] = Make(0x1F000000);
            r[Text] = Make(0xFF111113);
            r[TextSecondary] = Make(0xFF6E6E73);
            r[RingTrack] = Make(0xFFDCDCE0);
            r[Button] = Make(0xFFFFFFFF);
            r[ButtonHover] = Make(0xFFEDEDF0);
            r[ButtonPressed] = Make(0xFFE0E0E4);
            r[Menu] = Make(0xFFFFFFFF);
        }
        else
        {
            r[Background] = Make(0xFF000000);
            r[Surface] = Make(0xFF1C1C1E);
            r[Raised] = Make(0xFF333336);
            r[Border] = Make(0x1FFFFFFF);
            r[Text] = Make(0xFFFFFFFF);
            r[TextSecondary] = Make(0xFF8E8E93);
            r[RingTrack] = Make(0xFF2A2A2A);
            r[Button] = Make(0xFF202022);
            r[ButtonHover] = Make(0xFF2E2E31);
            r[ButtonPressed] = Make(0xFF161618);
            r[Menu] = Make(0xFF0A0A0A);
        }

        Palette.Use(ColorTheme, IsLight);
        Changed?.Invoke();
    }

    /// Current colour of a token, for code that paints outside the resource system (tinted logos).
    public static Color ColorOf(string token) =>
        Application.Current?.TryFindResource(token) is SolidColorBrush brush ? brush.Color : Colors.White;

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            bool light = WindowsUsesLightTheme();
            if (Theme == AppTheme.System && light != IsLight) Refresh();
        });
    }

    /// Windows "app mode": HKCU\…\Themes\Personalize\AppsUseLightTheme (1 = light). Read only; dark if absent.
    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (Exception ex)
        {
            Log.Warn("Theme", "could not read the Windows app mode: " + ex.GetType().Name);
            return false;
        }
    }

    private static SolidColorBrush Make(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
