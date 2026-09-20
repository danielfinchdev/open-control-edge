using System.Windows;
using System.Windows.Media;

namespace EdgeWidget.Ui;

/// Monochrome white icons drawn in a 24×24 coordinate space.
public static class Icons
{
    public static DrawingGroup ClaudeSpark { get; } = CreateSpark();
    public static DrawingGroup Codex { get; } = CreateCodex();
    public static DrawingGroup Grok { get; } = CreateGrok();
    public static DrawingGroup Cpu { get; } = CreateCpu();
    public static DrawingGroup Gpu { get; } = CreateGpu();
    public static DrawingGroup Refresh { get; } = CreateRefresh();
    public static DrawingGroup Power { get; } = CreatePower();

    public static DrawingImage ClaudeSparkImage { get; } = ToImage(ClaudeSpark);
    public static DrawingImage CodexImage { get; } = ToImage(Codex);
    public static DrawingImage GrokImage { get; } = ToImage(Grok);
    public static DrawingImage CpuImage { get; } = ToImage(Cpu);
    public static DrawingImage GpuImage { get; } = ToImage(Gpu);
    public static DrawingImage RefreshImage { get; } = ToImage(Refresh);
    public static DrawingImage PowerImage { get; } = ToImage(Power);

    private static DrawingGroup CreateSpark()
    {
        var group = NewGroup();
        double[] lengths = { 10.4, 7.6, 9.6, 8.2, 10.2, 7.4, 9.8, 8.0, 10.4, 7.8, 9.4, 8.4 };
        var rays = new StreamGeometry();
        using (var ctx = rays.Open())
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                double angle = (i * 30 - 90) * Math.PI / 180;
                Line(ctx, Polar(1.4, angle), Polar(lengths[i], angle));
            }
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(2.1), rays));
        group.Freeze();
        return group;
    }

    /// Codex: a terminal prompt inside a hexagon (a neutral glyph, not the OpenAI logo).
    private static DrawingGroup CreateCodex()
    {
        var group = NewGroup();
        var pen = RoundPen(1.8);

        var hexagon = new StreamGeometry();
        using (var ctx = hexagon.Open())
        {
            ctx.BeginFigure(Polar(9.6, Rad(-90)), false, true);
            for (int i = 1; i < 6; i++)
                ctx.LineTo(Polar(9.6, Rad(-90 + 60 * i)), true, true);
        }
        group.Children.Add(new GeometryDrawing(null, pen, hexagon));

        var prompt = new StreamGeometry();
        using (var ctx = prompt.Open())
        {
            ctx.BeginFigure(new Point(8.4, 9.4), false, false);
            ctx.LineTo(new Point(11, 12), true, true);
            ctx.LineTo(new Point(8.4, 14.6), true, true);
            Line(ctx, new Point(12.8, 14.6), new Point(15.8, 14.6));
        }
        group.Children.Add(new GeometryDrawing(null, pen, prompt));

        group.Freeze();
        return group;
    }

    /// Grok Bot: a bot head with an antenna (a neutral glyph, not the xAI logo).
    private static DrawingGroup CreateGrok()
    {
        var group = NewGroup();
        var pen = RoundPen(1.8);

        group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(4.6, 7.4, 14.8, 11.6), 3.4, 3.4)));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(9.4, 12.4), 1.35, 1.35)));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(14.6, 12.4), 1.35, 1.35)));
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(12, 3.6), 1.3, 1.3)));

        var details = new StreamGeometry();
        using (var ctx = details.Open())
        {
            Line(ctx, new Point(12, 4.9), new Point(12, 7.4));    // antenna stalk
            Line(ctx, new Point(9.6, 16), new Point(14.4, 16));   // mouth
            Line(ctx, new Point(2.6, 11.4), new Point(2.6, 14));  // side vents
            Line(ctx, new Point(21.4, 11.4), new Point(21.4, 14));
        }
        group.Children.Add(new GeometryDrawing(null, pen, details));

        group.Freeze();
        return group;
    }

    private static DrawingGroup CreateCpu()
    {
        var group = NewGroup();
        var pen = RoundPen(1.8);
        group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(5.5, 5.5, 13, 13), 2.6, 2.6)));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(9.25, 9.25, 5.5, 5.5), 1.2, 1.2)));
        var pins = new StreamGeometry();
        using (var ctx = pins.Open())
        {
            foreach (double p in new[] { 9.0, 12.0, 15.0 })
            {
                Line(ctx, new Point(p, 2.3), new Point(p, 5.5));
                Line(ctx, new Point(p, 18.5), new Point(p, 21.7));
                Line(ctx, new Point(2.3, p), new Point(5.5, p));
                Line(ctx, new Point(18.5, p), new Point(21.7, p));
            }
        }
        group.Children.Add(new GeometryDrawing(null, pen, pins));
        group.Freeze();
        return group;
    }

    /// A graphics card: board with a fan, two vents and the bracket pins underneath.
    private static DrawingGroup CreateGpu()
    {
        var group = NewGroup();
        var pen = RoundPen(1.8);
        group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(2.8, 5.5, 18.4, 11.5), 2.4, 2.4)));
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(9.3, 11.25), 3, 3)));
        var details = new StreamGeometry();
        using (var ctx = details.Open())
        {
            Line(ctx, new Point(15.2, 9.2), new Point(15.2, 13.3));
            Line(ctx, new Point(17.8, 9.2), new Point(17.8, 13.3));
            Line(ctx, new Point(6, 19.6), new Point(13.5, 19.6));
        }
        group.Children.Add(new GeometryDrawing(null, pen, details));
        group.Freeze();
        return group;
    }

    private static DrawingGroup CreateRefresh()
    {
        var group = NewGroup();
        const double radius = 7.5;
        double start = 0, end = 300;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(radius, Rad(start)), false, false);
            ctx.ArcTo(Polar(radius, Rad(end)), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, true);

            // Arrow head at the end of the arc, pointing along the clockwise tangent.
            Point tip = Polar(radius, Rad(end));
            var tangent = new Vector(-Math.Sin(Rad(end)), Math.Cos(Rad(end)));
            var radial = new Vector(Math.Cos(Rad(end)), Math.Sin(Rad(end)));
            ctx.BeginFigure(tip - tangent * 3.4 + radial * 3.0, false, false);
            ctx.LineTo(tip, true, true);
            ctx.LineTo(tip - tangent * 3.4 - radial * 3.0, true, true);
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(2.0), geometry));
        group.Freeze();
        return group;
    }

    private static DrawingGroup CreatePower()
    {
        var group = NewGroup();
        const double radius = 7.5;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(radius, Rad(-55)), false, false);
            ctx.ArcTo(Polar(radius, Rad(235)), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, true);
            Line(ctx, new Point(12, 3.2), new Point(12, 11));
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(2.0), geometry));
        group.Freeze();
        return group;
    }

    /// Transparent 24×24 box so every icon keeps identical bounds (and therefore identical scaling).
    private static DrawingGroup NewGroup()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        return group;
    }

    private static Pen RoundPen(double thickness) => new(Brushes.White, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    private static void Line(StreamGeometryContext ctx, Point from, Point to)
    {
        ctx.BeginFigure(from, false, false);
        ctx.LineTo(to, true, false);
    }

    private static Point Polar(double radius, double radians) =>
        new(12 + radius * Math.Cos(radians), 12 + radius * Math.Sin(radians));

    private static double Rad(double degrees) => degrees * Math.PI / 180;

    private static DrawingImage ToImage(Drawing drawing)
    {
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
