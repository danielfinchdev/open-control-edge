using System.Windows.Media;

namespace OpenControlEdge.Ui;

internal static class Palette
{
    public static readonly SolidColorBrush Green = Make(0x22, 0xC5, 0x5E);
    public static readonly SolidColorBrush Yellow = Make(0xE5, 0xE6, 0x19);
    public static readonly SolidColorBrush Orange = Make(0xE8, 0x49, 0x1D);
    public static readonly SolidColorBrush Red = Make(0xFF, 0x2D, 0x37);
    public static readonly SolidColorBrush Track = Make(0x2A, 0x2A, 0x2A);
    public static readonly SolidColorBrush Secondary = Make(0x8E, 0x8E, 0x93);

    /// Brand colours of the ring arcs.
    public static readonly SolidColorBrush Claude = Orange;
    public static readonly SolidColorBrush OpenAi = Green;
    public static readonly SolidColorBrush Other = Yellow;

    /// Above this usage a ring drops its brand colour and turns red (arc and percentage).
    public const double AlertPercent = 85;

    public static bool IsAlert(double percent) => percent > AlertPercent;

    /// Ring arc: the provider's colour, red once usage is above AlertPercent.
    public static SolidColorBrush ForRing(SolidColorBrush brand, double percent) =>
        IsAlert(percent) ? Red : brand;

    /// Card bars by level: &lt;50 green, &lt;70 yellow, up to 85 orange, above that red.
    public static SolidColorBrush ForPercent(double percent) =>
        percent < 50 ? Green : percent < 70 ? Yellow : IsAlert(percent) ? Red : Orange;

    /// Up to 70 °C green, up to 85 °C yellow, above that red.
    public static SolidColorBrush ForTemperature(double celsius) =>
        celsius <= 70 ? Green : celsius <= 85 ? Yellow : Red;

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
