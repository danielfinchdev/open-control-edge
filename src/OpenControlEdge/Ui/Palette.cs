using System.Windows.Media;
using OpenControlEdge.Services;

namespace OpenControlEdge.Ui;

/// Ring and bar colours of the active colour theme (set by ThemeManager). Red is shared by every theme: above
/// AlertPercent a ring drops its colour and turns red, whatever the theme.
internal static class Palette
{
    public static readonly SolidColorBrush Red = Make(0xFF, 0x00, 0x72);

    /// Default track of rings and bars; the views use the theme's Theme.RingTrack instead.
    public static readonly SolidColorBrush Track = Make(0x2A, 0x2A, 0x2A);

    /// Classic brand colours, kept as the defaults before ThemeManager applies a theme.
    public static SolidColorBrush Claude { get; private set; } = Make(0xE8, 0x49, 0x1D);
    public static SolidColorBrush OpenAi { get; private set; } = Make(0x22, 0xC5, 0x5E);
    public static SolidColorBrush Other { get; private set; } = Make(0xE5, 0xE6, 0x19);

    /// Card bars and temperature bands: low / medium / high. Above AlertPercent (or 85 °C) it is always Red.
    public static SolidColorBrush Low { get; private set; } = Make(0x22, 0xC5, 0x5E);
    public static SolidColorBrush Medium { get; private set; } = Make(0xE5, 0xE6, 0x19);
    public static SolidColorBrush High { get; private set; } = Make(0xE8, 0x49, 0x1D);

    /// Above this usage a ring drops its brand colour and turns red (arc and percentage).
    public const double AlertPercent = 85;

    public static bool IsAlert(double percent) => percent > AlertPercent;

    /// Ring arc: the provider's colour, red once usage is above AlertPercent.
    public static SolidColorBrush ForRing(SolidColorBrush brand, double percent) =>
        IsAlert(percent) ? Red : brand;

    /// Card bars by level: &lt;50 low, &lt;70 medium, up to 85 high, above that red.
    public static SolidColorBrush ForPercent(double percent) =>
        percent < 50 ? Low : percent < 70 ? Medium : IsAlert(percent) ? Red : High;

    /// Up to 70 °C low, up to 85 °C medium, above that red.
    public static SolidColorBrush ForTemperature(double celsius) =>
        celsius <= 70 ? Low : celsius <= 85 ? Medium : Red;

    /// Loads a colour theme. On a light panel, colours too pale to read against it are darkened.
    public static void Use(RingColorTheme theme, bool lightPanel)
    {
        (uint claude, uint openAi, uint other, uint low, uint medium, uint high) = theme switch
        {
            RingColorTheme.Mono => lightPanel
                ? (0x1C1C1Eu, 0x48484Au, 0x6E6E73u, 0x1C1C1Eu, 0x48484Au, 0x6E6E73u)
                : (0xF2F2F7u, 0xC7C7CCu, 0x9A9AA0u, 0xF2F2F7u, 0xC7C7CCu, 0x9A9AA0u),
            RingColorTheme.Ocean => (0x38BDF8u, 0x2DD4BFu, 0x818CF8u, 0x2DD4BFu, 0x38BDF8u, 0x818CF8u),
            RingColorTheme.Sunset => (0xFB923Cu, 0xF472B6u, 0xFACC15u, 0xFACC15u, 0xFB923Cu, 0xF97316u),
            RingColorTheme.Neon => (0xFFE600u, 0x39FF14u, 0x00E5FFu, 0x39FF14u, 0x00E5FFu, 0xFFE600u),
            _ => (0xE8491Du, 0x22C55Eu, 0xE5E619u, 0x22C55Eu, 0xE5E619u, 0xE8491Du),
        };

        Claude = Legible(claude, lightPanel);
        OpenAi = Legible(openAi, lightPanel);
        Other = Legible(other, lightPanel);
        Low = Legible(low, lightPanel);
        Medium = Legible(medium, lightPanel);
        High = Legible(high, lightPanel);
    }

    /// Scales a bright colour down until its relative luminance is at most 0.26 (3:1 against the light panel, the
    /// WCAG minimum for graphics), so yellows and neons stay visible. Hue is kept. Dark panels use them as they are.
    private static SolidColorBrush Legible(uint rgb, bool lightPanel)
    {
        double r = (rgb >> 16 & 0xFF) / 255.0, g = (rgb >> 8 & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        if (lightPanel)
        {
            for (int i = 0; i < 30 && Luminance(r, g, b) > 0.26; i++)
            {
                r *= 0.9;
                g *= 0.9;
                b *= 0.9;
            }
        }
        return Make((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double Luminance(double r, double g, double b) =>
        0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

    private static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
