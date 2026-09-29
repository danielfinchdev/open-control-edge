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
    public static DrawingGroup ChevronLeft { get; } = CreateChevron(left: true);
    public static DrawingGroup ChevronRight { get; } = CreateChevron(left: false);
    public static DrawingGroup Gear { get; } = CreateGear();
    public static DrawingGroup Close { get; } = CreateClose();

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

    /// Claude: the official Anthropic spark, filled. Path from Simple Icons (CC0 1.0),
    /// https://cdn.jsdelivr.net/npm/simple-icons/icons/claude.svg (24×24 viewBox), unchanged.
    private static DrawingGroup CreateSpark()
    {
        var group = NewGroup();
        Geometry mark = Geometry.Parse(
            "m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214" +
            "-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886" +
            "l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078" +
            ".6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733" +
            "-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0" +
            "l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457" +
            "l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517" +
            "-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321" +
            "l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607" +
            " 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607" +
            " 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107" +
            "-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575" +
            "-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214" +
            "-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468" +
            ".3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446" +
            "-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579" +
            "h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z").Clone();
        // Simple Icons fills the whole 24 box, the OpenAI and Cursor marks leave ~1 unit around: match their optical size.
        mark.Transform = new ScaleTransform(0.92, 0.92, 12, 12);
        group.Children.Add(new GeometryDrawing(Brushes.White, null, mark));
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

    /// Panel button: where the panel goes — right (into the screen edge) to hide it, left to bring it out.
    private static DrawingGroup CreateChevron(bool left)
    {
        var group = NewGroup();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double tip = left ? 8.5 : 15.5, back = left ? 14.5 : 9.5;
            ctx.BeginFigure(new Point(back, 5.5), false, false);
            ctx.LineTo(new Point(tip, 12), true, true);
            ctx.LineTo(new Point(back, 18.5), true, true);
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(2.2), geometry));
        group.Freeze();
        return group;
    }

    /// Settings: an eight-tooth gear with a round hub, outlined.
    private static DrawingGroup CreateGear()
    {
        var group = NewGroup();
        const int teeth = 8;
        const double outer = 9.4, root = 7.0, tipHalf = 9.0, rootHalf = 13.5;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < teeth; i++)
            {
                double c = i * 360.0 / teeth - 90;
                Point rootIn = Polar(root, Rad(c - rootHalf)), tipIn = Polar(outer, Rad(c - tipHalf));
                Point tipOut = Polar(outer, Rad(c + tipHalf)), rootOut = Polar(root, Rad(c + rootHalf));
                if (i == 0) ctx.BeginFigure(rootIn, false, true);
                else ctx.ArcTo(rootIn, new Size(root, root), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(tipIn, true, true);
                ctx.ArcTo(tipOut, new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(rootOut, true, true);
            }
            ctx.ArcTo(Polar(root, Rad(-90 - rootHalf)), new Size(root, root), 0, false, SweepDirection.Clockwise, true, true);
        }
        var pen = RoundPen(1.8);
        group.Children.Add(new GeometryDrawing(null, pen, geometry));
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(12, 12), 3, 3)));
        group.Freeze();
        return group;
    }

    private static DrawingGroup CreateClose()
    {
        var group = NewGroup();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            Line(ctx, new Point(6.5, 6.5), new Point(17.5, 17.5));
            Line(ctx, new Point(17.5, 6.5), new Point(6.5, 17.5));
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(2.2), geometry));
        group.Freeze();
        return group;
    }

    private static readonly Dictionary<(Drawing, Color), Drawing> TintCache = new();

    /// The same icon painted in another colour (dark logos on the light theme). White returns the original.
    /// Cached per icon and colour; call on the UI thread.
    public static Drawing Tint(Drawing icon, Color color)
    {
        if (color == Colors.White) return icon;
        if (TintCache.TryGetValue((icon, color), out Drawing? cached)) return cached;

        Drawing copy = icon.CloneCurrentValue();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Recolor(copy, brush);
        copy.Freeze();
        TintCache[(icon, color)] = copy;
        return copy;
    }

    private static void Recolor(Drawing drawing, Brush brush)
    {
        if (drawing is DrawingGroup group)
        {
            foreach (Drawing child in group.Children) Recolor(child, brush);
        }
        else if (drawing is GeometryDrawing geometry)
        {
            // The transparent sizing box stays transparent.
            if (geometry.Brush is SolidColorBrush fill && fill.Color.A > 0) geometry.Brush = brush;
            if (geometry.Pen is Pen pen)
            {
                Pen recoloured = pen.Clone();
                recoloured.Brush = brush;
                geometry.Pen = recoloured;
            }
        }
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
