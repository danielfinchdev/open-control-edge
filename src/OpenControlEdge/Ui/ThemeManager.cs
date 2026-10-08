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

    /// The panel background chosen in Settings > Personalización; null is the theme's own.
    public static Color? PanelBackground { get; private set; }

    /// True while the light tokens are in use (Light, or System with Windows in light mode).
    public static bool IsLight { get; private set; }

    /// Raised on the UI thread after the tokens and Palette have changed.
    public static event Action? Changed;

    /// Keeps the panel background that is already set.
    public static void Apply(AppTheme theme, RingColorTheme colors) => Apply(theme, colors, PanelBackground);

    /// A custom background only changes when it is a different colour (no repaint otherwise).
    public static void ApplyBackground(Color? background)
    {
        if (background == PanelBackground) return;
        Apply(Theme, ColorTheme, background);
    }

    public static void Apply(AppTheme theme, RingColorTheme colors, Color? background)
    {
        PanelBackground = background;
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

        // A background of the user's own: text, track and buttons are mixed from it and from the text colour that reads
        // on it, so a pale background gets dark text (and the light ring colours) whatever the theme.
        bool lightPanel = IsLight;
        if (PanelBackground is Color custom)
        {
            lightPanel = RelativeLuminance(custom) > 0.4;
            Color text = lightPanel ? Color.FromRgb(0x11, 0x11, 0x13) : Colors.White;
            Color secondary = lightPanel ? Color.FromRgb(0x5E, 0x5E, 0x63) : Color.FromRgb(0x9A, 0x9A, 0xA0);
            r[Background] = Make(custom);
            r[Surface] = Make(Mix(custom, text, 0.10));
            r[Raised] = Make(Mix(custom, text, 0.20));
            r[Border] = Make(Color.FromArgb(0x1F, text.R, text.G, text.B));
            r[Text] = Make(text);
            r[TextSecondary] = Make(secondary);
            r[RingTrack] = Make(Mix(custom, text, 0.14));
            r[Button] = Make(Mix(custom, text, 0.11));
            r[ButtonHover] = Make(Mix(custom, text, 0.18));
            r[ButtonPressed] = Make(Mix(custom, text, 0.06));
            r[Menu] = Make(Mix(custom, lightPanel ? Colors.White : Colors.Black, 0.35));
        }

        Palette.Use(ColorTheme, lightPanel);
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

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));

    /// WCAG relative luminance (0 black, 1 white).
    internal static double RelativeLuminance(Color c)
    {
        static double Linear(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    private static SolidColorBrush Make(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Make(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
