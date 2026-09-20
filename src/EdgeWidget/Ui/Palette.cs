using System.Windows.Media;

namespace EdgeWidget.Ui;

internal static class Palette
{
    public static readonly SolidColorBrush Green = Make(0x30, 0xD1, 0x58);
    public static readonly SolidColorBrush Yellow = Make(0xFF, 0xD6, 0x0A);
    public static readonly SolidColorBrush Red = Make(0xFF, 0x45, 0x3A);
    public static readonly SolidColorBrush ClaudeOrange = Make(0xD9, 0x77, 0x57);
    public static readonly SolidColorBrush Track = Make(0x2A, 0x2A, 0x2A);
    public static readonly SolidColorBrush Secondary = Make(0x8E, 0x8E, 0x93);

    /// &lt;50 green, &lt;80 yellow, otherwise red.
    public static SolidColorBrush ForPercent(double percent) =>
        percent < 50 ? Green : percent < 80 ? Yellow : Red;

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
