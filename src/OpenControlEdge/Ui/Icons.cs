using System.Windows;
using System.Windows.Media;

namespace OpenControlEdge.Ui;

/// Monochrome white icons drawn in a 24×24 coordinate space.
public static class Icons
{
    public static DrawingGroup ClaudeSpark { get; } = CreateSpark();
    public static DrawingGroup Codex { get; } = CreateCodex();
    public static DrawingGroup Cursor { get; } = CreateCursor();
    public static DrawingGroup OpenCode { get; } = CreateOpenCode();
    public static DrawingGroup DeepSeek { get; } = CreateDeepSeek();
    public static DrawingGroup OpenRouter { get; } = CreateOpenRouter();
    public static DrawingGroup Cpu { get; } = CreateCpu();
    public static DrawingGroup Gpu { get; } = CreateGpu();
    public static DrawingGroup Refresh { get; } = CreateRefresh();
    public static DrawingGroup Power { get; } = CreatePower();
    public static DrawingGroup Key { get; } = CreateKey();

    public static DrawingImage ClaudeSparkImage { get; } = ToImage(ClaudeSpark);
    public static DrawingImage CodexImage { get; } = ToImage(Codex);
    public static DrawingImage CursorImage { get; } = ToImage(Cursor);
    public static DrawingImage OpenCodeImage { get; } = ToImage(OpenCode);
    public static DrawingImage DeepSeekImage { get; } = ToImage(DeepSeek);
    public static DrawingImage OpenRouterImage { get; } = ToImage(OpenRouter);
    public static DrawingImage CpuImage { get; } = ToImage(Cpu);
    public static DrawingImage GpuImage { get; } = ToImage(Gpu);
    public static DrawingImage RefreshImage { get; } = ToImage(Refresh);
    public static DrawingImage PowerImage { get; } = ToImage(Power);
    public static DrawingImage KeyImage { get; } = ToImage(Key);

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

    /// Cursor: a monochrome isometric cube mark.
    private static DrawingGroup CreateCursor()
    {
        var group = NewGroup();
        var outline = new StreamGeometry();
        using (var ctx = outline.Open())
        {
            ctx.BeginFigure(new Point(12, 2.8), false, true);
            ctx.LineTo(new Point(20.1, 7.4), true, true);
            ctx.LineTo(new Point(20.1, 16.6), true, true);
            ctx.LineTo(new Point(12, 21.2), true, true);
            ctx.LineTo(new Point(3.9, 16.6), true, true);
            ctx.LineTo(new Point(3.9, 7.4), true, true);
            ctx.LineTo(new Point(12, 2.8), true, true);
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), outline));
        var facets = new StreamGeometry();
        using (var ctx = facets.Open())
        {
            Line(ctx, new Point(12, 2.8), new Point(12, 12));
            Line(ctx, new Point(3.9, 7.4), new Point(12, 12));
            Line(ctx, new Point(20.1, 7.4), new Point(12, 12));
            Line(ctx, new Point(12, 12), new Point(12, 21.2));
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.5), facets));

        group.Freeze();
        return group;
    }

    private static DrawingGroup CreateOpenCode()
    {
        var group = NewGroup();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(9, 5), false, false);
            ctx.LineTo(new Point(4, 12), true, true);
            ctx.LineTo(new Point(9, 19), true, true);
            ctx.BeginFigure(new Point(15, 5), false, false);
            ctx.LineTo(new Point(20, 12), true, true);
            ctx.LineTo(new Point(15, 19), true, true);
            Line(ctx, new Point(11, 17), new Point(13, 7));
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(2), geometry));
        group.Freeze();
        return group;
    }

    private static DrawingGroup CreateDeepSeek()
    {
        var group = NewGroup();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(4, 8), false, false);
            ctx.BezierTo(new Point(7, 4), new Point(11, 4), new Point(13, 8), true, true);
            ctx.BezierTo(new Point(15, 12), new Point(18, 12), new Point(20, 8), true, true);
            ctx.BeginFigure(new Point(4, 16), false, false);
            ctx.BezierTo(new Point(7, 12), new Point(11, 12), new Point(13, 16), true, true);
            ctx.BezierTo(new Point(15, 20), new Point(18, 20), new Point(20, 16), true, true);
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.9), geometry));
        group.Freeze();
        return group;
    }

    private static DrawingGroup CreateOpenRouter()
    {
        var group = NewGroup();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(6, 6), false, false);
            ctx.BezierTo(new Point(12, 6), new Point(12, 18), new Point(18, 18), true, true);
            ctx.BeginFigure(new Point(6, 18), false, false);
            ctx.BezierTo(new Point(12, 18), new Point(12, 6), new Point(18, 6), true, true);
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), geometry));
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), new EllipseGeometry(new Point(5, 6), 2.2, 2.2)));
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), new EllipseGeometry(new Point(19, 6), 2.2, 2.2)));
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), new EllipseGeometry(new Point(5, 18), 2.2, 2.2)));
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), new EllipseGeometry(new Point(19, 18), 2.2, 2.2)));
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

    private static DrawingGroup CreateKey()
    {
        var group = NewGroup();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(14.5, 9.5), false, false);
            ctx.ArcTo(new Point(7.5, 16.5), new Size(5, 5), 0, false, SweepDirection.Clockwise, true, true);
            Line(ctx, new Point(11.1, 12.9), new Point(19.8, 4.2));
            Line(ctx, new Point(16.7, 7.3), new Point(19.2, 9.8));
            Line(ctx, new Point(14.5, 9.5), new Point(17, 12));
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.9), geometry));
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
