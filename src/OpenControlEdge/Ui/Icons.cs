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
    public static DrawingGroup Ram { get; } = CreateRam();
    public static DrawingGroup Refresh { get; } = CreateRefresh();
    public static DrawingGroup Power { get; } = CreatePower();
    public static DrawingGroup Key { get; } = CreateKey();
    public static DrawingGroup ChevronLeft { get; } = CreateChevron(left: true);
    public static DrawingGroup ChevronRight { get; } = CreateChevron(left: false);
    public static DrawingGroup Gear { get; } = CreateGear();
    public static DrawingGroup Close { get; } = CreateClose();
    public static DrawingGroup Swatch { get; } = CreateSwatch();
    public static DrawingGroup Bot { get; } = CreateBot();
    public static DrawingGroup Info { get; } = CreateInfo();
    public static DrawingGroup Message { get; } = CreateMessage();

    public static DrawingImage ClaudeSparkImage { get; } = ToImage(ClaudeSpark);
    public static DrawingImage CodexImage { get; } = ToImage(Codex);
    public static DrawingImage CursorImage { get; } = ToImage(Cursor);
    public static DrawingImage OpenCodeImage { get; } = ToImage(OpenCode);
    public static DrawingImage DeepSeekImage { get; } = ToImage(DeepSeek);
    public static DrawingImage OpenRouterImage { get; } = ToImage(OpenRouter);
    public static DrawingImage CpuImage { get; } = ToImage(Cpu);
    public static DrawingImage GpuImage { get; } = ToImage(Gpu);
    public static DrawingImage RamImage { get; } = ToImage(Ram);
    public static DrawingImage RefreshImage { get; } = ToImage(Refresh);
    public static DrawingImage PowerImage { get; } = ToImage(Power);
    public static DrawingImage KeyImage { get; } = ToImage(Key);

    // ───── Brand logos ─────
    // Paths from @lobehub/icons-static-svg 1.95.1 (MIT License, © LobeHub),
    // https://cdn.jsdelivr.net/npm/@lobehub/icons-static-svg@1.95.1/icons/<name>.svg, 24×24 viewBox, fill-rule evenodd.
    // Only the SVG arc flags are spaced out for the WPF path parser; every figure and coordinate is unchanged.
    // The marks are trademarks of their owners (see THIRD-PARTY-NOTICES.txt). Each one is fitted to the 24 box with
    // its own optical size so a thin star and a solid block weigh the same inside the ring.

    /// Claude (Anthropic): the spark. icons/claude.svg.
    private static DrawingGroup CreateSpark() => Brand(23.6,
        "M 4.709 15.955 l 4.72 -2.647 .08 -.23 -.08 -.128 H 9.2 l -.79 -.048 -2.698 -.073 -2.339 -.097 -2.266 -.122 -.571 -.121 L 0 11.784 " +
        "l .055 -.352 .48 -.321 .686 .06 1.52 .103 2.278 .158 1.652 .097 2.449 .255 h .389 l .055 -.157 -.134 -.098 -.103 -.097 " +
        "-2.358 -1.596 -2.552 -1.688 -1.336 -.972 -.724 -.491 -.364 -.462 -.158 -1.008 .656 -.722 .881 .06 .225 .061 .893 .686 " +
        "1.908 1.476 2.491 1.833 .365 .304 .145 -.103 .019 -.073 -.164 -.274 -1.355 -2.446 -1.446 -2.49 -.644 -1.032 -.17 -.619 " +
        "a 2.97 2.97 0 0 1 -.104 -.729 L 6.283 .134 6.696 0 l .996 .134 .42 .364 .62 1.414 1.002 2.229 1.555 3.03 .456 .898 .243 .832 " +
        ".091 .255 h .158 V 9.01 l .128 -1.706 .237 -2.095 .23 -2.695 .08 -.76 .376 -.91 .747 -.492 .584 .28 .48 .685 -.067 .444 " +
        "-.286 1.851 -.559 2.903 -.364 1.942 h .212 l .243 -.242 .985 -1.306 1.652 -2.064 .73 -.82 .85 -.904 .547 -.431 h 1.033 " +
        "l .76 1.129 -.34 1.166 -1.064 1.347 -.881 1.142 -1.264 1.7 -.79 1.36 .073 .11 .188 -.02 2.856 -.606 1.543 -.28 1.841 -.315 " +
        ".833 .388 .091 .395 -.328 .807 -1.969 .486 -2.309 .462 -3.439 .813 -.042 .03 .049 .061 1.549 .146 .662 .036 h 1.622 " +
        "l 3.02 .225 .79 .522 .474 .638 -.079 .485 -1.215 .62 -1.64 -.389 -3.829 -.91 -1.312 -.329 h -.182 v .11 l 1.093 1.068 " +
        "2.006 1.81 2.509 2.33 .127 .578 -.322 .455 -.34 -.049 -2.205 -1.657 -.851 -.747 -1.926 -1.62 h -.128 v .17 l .444 .649 " +
        "2.345 3.521 .122 1.08 -.17 .353 -.608 .213 -.668 -.122 -1.374 -1.925 -1.415 -2.167 -1.143 -1.943 -.14 .08 -.674 7.254 " +
        "-.316 .37 -.729 .28 -.607 -.461 -.322 -.747 .322 -1.476 .389 -1.924 .315 -1.53 .286 -1.9 .17 -.632 -.012 -.042 -.14 .018 " +
        "-1.434 1.967 -2.18 2.945 -1.726 1.845 -.414 .164 -.717 -.37 .067 -.662 .401 -.589 2.388 -3.036 1.44 -1.882 .93 -1.086 " +
        "-.006 -.158 h -.055 L 4.132 18.56 l -1.13 .146 -.487 -.456 .061 -.746 .231 -.243 1.908 -1.312 -.006 .006 z");

    /// Codex / ChatGPT: the OpenAI blossom. icons/openai.svg.
    private static DrawingGroup CreateCodex() => Brand(21.4,
        "M 9.205 8.658 v -2.26 c 0 -.19 .072 -.333 .238 -.428 l 4.543 -2.616 c .619 -.357 1.356 -.523 2.117 -.523 2.854 0 4.662 2.212 " +
        "4.662 4.566 0 .167 0 .357 -.024 .547 l -4.71 -2.759 a .797 .797 0 0 0 -.856 0 l -5.97 3.473 z " +
        "m 10.609 8.8 V 12.06 c 0 -.333 -.143 -.57 -.429 -.737 l -5.97 -3.473 1.95 -1.118 a .433 .433 0 0 1 .476 0 l 4.543 2.617 " +
        "c 1.309 .76 2.189 2.378 2.189 3.948 0 1.808 -1.07 3.473 -2.76 4.163 z " +
        "M 7.802 12.703 l -1.95 -1.142 c -.167 -.095 -.239 -.238 -.239 -.428 V 5.899 c 0 -2.545 1.95 -4.472 4.591 -4.472 1 0 1.927 .333 " +
        "2.712 .928 L 8.23 5.067 c -.285 .166 -.428 .404 -.428 .737 v 6.898 z " +
        "M 12 15.128 l -2.795 -1.57 v -3.33 L 12 8.658 l 2.795 1.57 v 3.33 L 12 15.128 z " +
        "m 1.796 7.23 c -1 0 -1.927 -.332 -2.712 -.927 l 4.686 -2.712 c .285 -.166 .428 -.404 .428 -.737 v -6.898 l 1.974 1.142 " +
        "c .167 .095 .238 .238 .238 .428 v 5.233 c 0 2.545 -1.974 4.472 -4.614 4.472 z " +
        "m -5.637 -5.303 l -4.544 -2.617 c -1.308 -.761 -2.188 -2.378 -2.188 -3.948 A 4.482 4.482 0 0 1 4.21 6.327 v 5.423 " +
        "c 0 .333 .143 .571 .428 .738 l 5.947 3.449 -1.95 1.118 a .432 .432 0 0 1 -.476 0 z " +
        "m -.262 3.9 c -2.688 0 -4.662 -2.021 -4.662 -4.519 0 -.19 .024 -.38 .047 -.57 l 4.686 2.71 c .286 .167 .571 .167 .856 0 " +
        "l 5.97 -3.448 v 2.26 c 0 .19 -.07 .333 -.237 .428 l -4.543 2.616 c -.619 .357 -1.356 .523 -2.117 .523 z " +
        "m 5.899 2.83 a 5.947 5.947 0 0 0 5.827 -4.756 C 22.287 18.339 24 15.84 24 13.296 c 0 -1.665 -.713 -3.282 -1.998 -4.448 " +
        ".119 -.5 .19 -.999 .19 -1.498 0 -3.401 -2.759 -5.947 -5.946 -5.947 -.642 0 -1.26 .095 -1.88 .31 A 5.962 5.962 0 0 0 10.205 0 " +
        "a 5.947 5.947 0 0 0 -5.827 4.757 C 1.713 5.447 0 7.945 0 10.49 c 0 1.666 .713 3.283 1.998 4.448 -.119 .5 -.19 1 -.19 1.499 " +
        "0 3.401 2.759 5.946 5.946 5.946 .642 0 1.26 -.095 1.88 -.309 a 5.96 5.96 0 0 0 4.162 1.713 z");

    /// Cursor: the isometric cube with the arrow facet (the 2D mark of the current logo). icons/cursor.svg.
    private static DrawingGroup CreateCursor() => Brand(21.6,
        "M 22.106 5.68 L 12.5 .135 a .998 .998 0 0 0 -.998 0 L 1.893 5.68 a .84 .84 0 0 0 -.419 .726 v 11.186 c 0 .3 .16 .577 .42 .727 " +
        "l 9.607 5.547 a .999 .999 0 0 0 .998 0 l 9.608 -5.547 a .84 .84 0 0 0 .42 -.727 V 6.407 a .84 .84 0 0 0 -.42 -.726 z " +
        "m -.603 1.176 L 12.228 22.92 c -.063 .108 -.228 .064 -.228 -.061 V 12.34 a .59 .59 0 0 0 -.295 -.51 l -9.11 -5.26 " +
        "c -.107 -.062 -.063 -.228 .062 -.228 h 18.55 c .264 0 .428 .286 .296 .514 z");

    /// OpenCode: the square "O" block with its counter. icons/opencode.svg.
    private static DrawingGroup CreateOpenCode() => Brand(18.2,
        "M 16 6 H 8 v 12 h 8 V 6 z m 4 16 H 4 V 2 h 16 v 20 z");

    /// DeepSeek: the whale. icons/deepseek.svg.
    private static DrawingGroup CreateDeepSeek() => Brand(23.4,
        "M 23.748 4.482 c -.254 -.124 -.364 .113 -.512 .234 -.051 .039 -.094 .09 -.137 .136 -.372 .397 -.806 .657 -1.373 .626 " +
        "-.829 -.046 -1.537 .214 -2.163 .848 -.133 -.782 -.575 -1.248 -1.247 -1.548 -.352 -.156 -.708 -.311 -.955 -.65 -.172 -.241 " +
        "-.219 -.51 -.305 -.774 -.055 -.16 -.11 -.323 -.293 -.35 -.2 -.031 -.278 .136 -.356 .276 -.313 .572 -.434 1.202 -.422 1.84 " +
        ".027 1.436 .633 2.58 1.838 3.393 .137 .093 .172 .187 .129 .323 -.082 .28 -.18 .552 -.266 .833 -.055 .179 -.137 .217 -.329 .14 " +
        "a 5.526 5.526 0 0 1 -1.736 -1.18 c -.857 -.828 -1.631 -1.742 -2.597 -2.458 a 11.365 11.365 0 0 0 -.689 -.471 " +
        "c -.985 -.957 .13 -1.743 .388 -1.836 .27 -.098 .093 -.432 -.779 -.428 -.872 .004 -1.67 .295 -2.687 .684 " +
        "a 3.055 3.055 0 0 1 -.465 .137 9.597 9.597 0 0 0 -2.883 -.102 c -1.885 .21 -3.39 1.102 -4.497 2.623 " +
        "C .082 8.606 -.231 10.684 .152 12.85 c .403 2.284 1.569 4.175 3.36 5.653 1.858 1.533 3.997 2.284 6.438 2.14 1.482 -.085 " +
        "3.133 -.284 4.994 -1.86 .47 .234 .962 .327 1.78 .397 .63 .059 1.236 -.03 1.705 -.128 .735 -.156 .684 -.837 .419 -.961 " +
        "-2.155 -1.004 -1.682 -.595 -2.113 -.926 1.096 -1.296 2.746 -2.642 3.392 -7.003 .05 -.347 .007 -.565 0 -.845 -.004 -.17 " +
        ".035 -.237 .23 -.256 a 4.173 4.173 0 0 0 1.545 -.475 c 1.396 -.763 1.96 -2.015 2.093 -3.517 .02 -.23 -.004 -.467 -.247 -.588 z " +
        "M 11.581 18 c -2.089 -1.642 -3.102 -2.183 -3.52 -2.16 -.392 .024 -.321 .471 -.235 .763 .09 .288 .207 .486 .371 .739 " +
        ".114 .167 .192 .416 -.113 .603 -.673 .416 -1.842 -.14 -1.897 -.167 -1.361 -.802 -2.5 -1.86 -3.301 -3.307 -.774 -1.393 " +
        "-1.224 -2.887 -1.298 -4.482 -.02 -.386 .093 -.522 .477 -.592 a 4.696 4.696 0 0 1 1.529 -.039 c 2.132 .312 3.946 1.265 " +
        "5.468 2.774 .868 .86 1.525 1.887 2.202 2.891 .72 1.066 1.494 2.082 2.48 2.914 .348 .292 .625 .514 .891 .677 -.802 .09 " +
        "-2.14 .11 -3.054 -.614 z " +
        "m 1 -6.44 a .306 .306 0 0 1 .415 -.287 .302 .302 0 0 1 .2 .288 .306 .306 0 0 1 -.31 .307 .303 .303 0 0 1 -.304 -.308 z " +
        "m 3.11 1.596 c -.2 .081 -.399 .151 -.59 .16 a 1.245 1.245 0 0 1 -.798 -.254 c -.274 -.23 -.47 -.358 -.552 -.758 " +
        "a 1.73 1.73 0 0 1 .016 -.588 c .07 -.327 -.008 -.537 -.239 -.727 -.187 -.156 -.426 -.199 -.688 -.199 " +
        "a .559 .559 0 0 1 -.254 -.078 c -.11 -.054 -.2 -.19 -.114 -.358 .028 -.054 .16 -.186 .192 -.21 .356 -.202 .767 -.136 " +
        "1.146 .016 .352 .144 .618 .408 1.001 .782 .391 .451 .462 .576 .685 .914 .176 .265 .336 .537 .445 .848 .067 .195 -.019 .354 " +
        "-.25 .452 z");

    /// OpenRouter: the current mark (a large and a small disc joined by the route). icons/openrouter.svg.
    private static DrawingGroup CreateOpenRouter() => Brand(23.2,
        "M 18.654 3.87 a 5.087 5.087 0 1 1 0 10.174 L 23.7 19.09 c .64 .641 .187 1.737 -.72 1.737 H 8.48 " +
        "a 8.479 8.479 0 0 1 0 -16.958 h 10.175 z M 8.479 7.26 a 5.087 5.087 0 1 0 0 10.176 5.087 5.087 0 0 0 0 -10.175 z");

    /// A filled logo scaled so its larger side measures `size` and centred in the 24 box.
    private static DrawingGroup Brand(double size, string path)
    {
        var group = NewGroup();
        // Path markup without an F prefix is EvenOdd, like the SVGs.
        Geometry mark = Geometry.Parse(path).Clone();
        Rect bounds = mark.Bounds;
        double scale = size / Math.Max(bounds.Width, bounds.Height);
        var fit = new TransformGroup();
        fit.Children.Add(new TranslateTransform(-(bounds.Left + bounds.Width / 2), -(bounds.Top + bounds.Height / 2)));
        fit.Children.Add(new ScaleTransform(scale, scale));
        fit.Children.Add(new TranslateTransform(12, 12));
        mark.Transform = fit;
        group.Children.Add(new GeometryDrawing(Brushes.White, null, mark));
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

    /// A memory module: board with three chips, the notch and the contacts underneath.
    private static DrawingGroup CreateRam()
    {
        var group = NewGroup();
        var pen = RoundPen(1.8);
        group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(2.8, 6, 18.4, 10), 2, 2)));
        foreach (double x in new[] { 5.6, 10.1, 14.6 })
            group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(x, 8.6, 3.8, 4.8), 0.8, 0.8)));
        var contacts = new StreamGeometry();
        using (var ctx = contacts.Open())
        {
            foreach (double x in new[] { 5.5, 8.5, 15.5, 18.5 }) Line(ctx, new Point(x, 16), new Point(x, 19.4));
        }
        group.Children.Add(new GeometryDrawing(null, pen, contacts));
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

    /// Settings "Appearance": a round palette with four paint dots.
    private static DrawingGroup CreateSwatch()
    {
        var group = NewGroup();
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), new EllipseGeometry(new Point(12, 12), 8.6, 8.6)));
        foreach (Point dot in new[] { new Point(7.4, 13), new Point(8.4, 8.9), new Point(12, 7), new Point(15.6, 8.9) })
            group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(dot, 1.35, 1.35)));
        group.Freeze();
        return group;
    }

    /// Settings "Agents": a robot head with an antenna.
    private static DrawingGroup CreateBot()
    {
        var group = NewGroup();
        var pen = RoundPen(1.8);
        group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(4.5, 8, 15, 11.5), 3, 3)));
        var lines = new StreamGeometry();
        using (var ctx = lines.Open())
        {
            Line(ctx, new Point(12, 4.2), new Point(12, 8));
            Line(ctx, new Point(9.3, 12.6), new Point(9.3, 14.6));
            Line(ctx, new Point(14.7, 12.6), new Point(14.7, 14.6));
        }
        group.Children.Add(new GeometryDrawing(null, pen, lines));
        group.Freeze();
        return group;
    }

    /// Settings "About": an i in a circle.
    private static DrawingGroup CreateInfo()
    {
        var group = NewGroup();
        var pen = RoundPen(1.8);
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(12, 12), 8.8, 8.8)));
        var stem = new StreamGeometry();
        using (var ctx = stem.Open()) Line(ctx, new Point(12, 11.2), new Point(12, 16.2));
        group.Children.Add(new GeometryDrawing(null, pen, stem));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(12, 7.9), 1.15, 1.15)));
        group.Freeze();
        return group;
    }

    /// Settings "Feedback": a speech bubble.
    private static DrawingGroup CreateMessage()
    {
        var group = NewGroup();
        var bubble = new StreamGeometry();
        using (var ctx = bubble.Open())
        {
            ctx.BeginFigure(new Point(7, 4.5), false, true);
            ctx.LineTo(new Point(17, 4.5), true, true);
            ctx.ArcTo(new Point(20, 7.5), new Size(3, 3), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(20, 13.5), true, true);
            ctx.ArcTo(new Point(17, 16.5), new Size(3, 3), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(10, 16.5), true, true);
            ctx.LineTo(new Point(6, 19.8), true, true);
            ctx.LineTo(new Point(6, 16.4), true, true);
            ctx.ArcTo(new Point(4, 13.5), new Size(3, 3), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(4, 7.5), true, true);
            ctx.ArcTo(new Point(7, 4.5), new Size(3, 3), 0, false, SweepDirection.Clockwise, true, true);
        }
        group.Children.Add(new GeometryDrawing(null, RoundPen(1.8), bubble));
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
