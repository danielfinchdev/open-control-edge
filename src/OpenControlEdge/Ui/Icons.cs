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

    /// Claude: the radial burst — irregular rays that are thin at the centre and widen towards a rounded tip.
    private static DrawingGroup CreateSpark()
    {
        var group = NewGroup();
        double[] lengths = { 11.6, 9.4, 11.0, 9.8, 11.4, 9.1, 11.2, 9.6, 11.6, 9.3, 10.8, 9.9 };
        var rays = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = rays.Open())
        {
            const double innerHalf = 0.3, outerHalf = 0.95, inner = 1.1;
            for (int i = 0; i < lengths.Length; i++)
            {
                double angle = Rad(i * 30 - 90 + (i % 2 == 0 ? 0 : 4));
                var along = new Vector(Math.Cos(angle), Math.Sin(angle));
                var across = new Vector(-along.Y, along.X);
                double tip = lengths[i] - outerHalf;
                Point center = Polar(0, angle);
                ctx.BeginFigure(center + along * inner - across * innerHalf, true, true);
                ctx.LineTo(center + along * tip - across * outerHalf, true, true);
                ctx.ArcTo(center + along * tip + across * outerHalf, new Size(outerHalf, outerHalf), 0, false,
                    SweepDirection.Counterclockwise, true, true);
                ctx.LineTo(center + along * inner + across * innerHalf, true, true);
            }
        }
        group.Children.Add(new GeometryDrawing(Brushes.White, null, rays));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(12, 12), 1.9, 1.9)));
        group.Freeze();
        return group;
    }

    /// Codex / ChatGPT: the OpenAI blossom (six interlocking links around a hexagon), filled.
    private static DrawingGroup CreateCodex()
    {
        var group = NewGroup();
        var mark = Geometry.Parse(
            "M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818" +
            "a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001" +
            "A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729z" +
            "m-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686" +
            "a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944z" +
            "m-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324" +
            "a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464z" +
            "M2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685" +
            "a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872z" +
            "m16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772" +
            "a.79.79 0 0 0-.407-.667z" +
            "m2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615" +
            "l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66z" +
            "M8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459" +
            "a.7948.7948 0 0 0-.3927.6813z" +
            "m1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997z");
        group.Children.Add(new GeometryDrawing(Brushes.White, null, mark));
        group.Freeze();
        return group;
    }

    /// Cursor: the hexagonal cube with the pointer-shaped facet cut out of it.
    private static DrawingGroup CreateCursor()
    {
        var group = NewGroup();
        var mark = Geometry.Parse(
            "M11.503.131 1.891 5.678a.84.84 0 0 0-.42.726v11.188c0 .3.162.575.42.724l9.609 5.55a1 1 0 0 0 .998 0l9.61-5.55" +
            "a.84.84 0 0 0 .42-.724V6.404a.84.84 0 0 0-.42-.726L12.497.131a1.01 1.01 0 0 0-.996 0" +
            "M2.657 6.338h18.55c.263 0 .43.287.297.515L12.23 22.918c-.062.107-.229.064-.229-.06V12.335a.59.59 0 0 0-.295-.51" +
            "l-9.11-5.257c-.109-.063-.064-.23.061-.23");
        group.Children.Add(new GeometryDrawing(Brushes.White, null, mark));
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

    /// DeepSeek: the whale, head to the left and the tail curling up on the right.
    private static DrawingGroup CreateDeepSeek()
    {
        var group = NewGroup();
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(2.2, 12.8), true, true);
            ctx.BezierTo(new Point(2.2, 8.6), new Point(5.8, 6.0), new Point(10.0, 6.0), true, true);
            ctx.BezierTo(new Point(14.2, 6.0), new Point(17.0, 8.2), new Point(18.4, 11.0), true, true);
            ctx.BezierTo(new Point(19.0, 9.2), new Point(20.2, 7.2), new Point(22.0, 6.2), true, true);
            ctx.BezierTo(new Point(21.9, 8.4), new Point(21.4, 10.6), new Point(20.2, 12.2), true, true);
            ctx.BezierTo(new Point(19.4, 16.4), new Point(15.4, 18.8), new Point(11.0, 18.8), true, true);
            ctx.BezierTo(new Point(6.0, 18.8), new Point(2.2, 16.6), new Point(2.2, 12.8), true, true);
        }
        // Eye and the flipper line are cut out, so the ring's black shows through.
        var cuts = new GeometryGroup();
        cuts.Children.Add(new EllipseGeometry(new Point(6.8, 11.2), 1.05, 1.05));
        cuts.Children.Add(new PathGeometry(new[] { new PathFigure(new Point(8.6, 15.6), new PathSegment[]
        {
            new BezierSegment(new Point(10.6, 16.4), new Point(13.4, 16.0), new Point(15.2, 13.6), true),
            new BezierSegment(new Point(13.6, 14.8), new Point(11.0, 15.2), new Point(8.6, 15.6), true),
        }, true) }));
        var whale = new CombinedGeometry(GeometryCombineMode.Exclude, body, cuts);
        group.Children.Add(new GeometryDrawing(Brushes.White, null, whale));
        group.Freeze();
        return group;
    }

    /// OpenRouter: one route that forks into two arrows.
    private static DrawingGroup CreateOpenRouter()
    {
        var group = NewGroup();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(2.8, 12), false, false);
            ctx.LineTo(new Point(7.2, 12), true, true);
            ctx.BezierTo(new Point(11.4, 12), new Point(12.2, 6.4), new Point(19.6, 6.4), true, true);
            ctx.BeginFigure(new Point(7.2, 12), false, false);
            ctx.BezierTo(new Point(11.4, 12), new Point(12.2, 17.6), new Point(19.6, 17.6), true, true);
            ctx.BeginFigure(new Point(16.6, 3.6), false, false);
            ctx.LineTo(new Point(19.8, 6.4), true, true);
            ctx.LineTo(new Point(16.6, 9.2), true, true);
            ctx.BeginFigure(new Point(16.6, 14.8), false, false);
            ctx.LineTo(new Point(19.8, 17.6), true, true);
            ctx.LineTo(new Point(16.6, 20.4), true, true);
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(2.0), geometry));
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
