using System.Windows;
using System.Windows.Media;

namespace OpenControlEdge.Ui;

/// Panel background: a tab glued to the right screen edge. Its left corners are rounded (CornerRadius) and, above
/// and below the body, concave flares (FlareHeight tall) run back into the screen edge like an inverted notch.
/// The body spans FlareHeight … ActualHeight - FlareHeight; the right edge of the element is the screen edge.
public sealed class EdgeTabShape : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(EdgeTabShape),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(EdgeTabShape),
        new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FlareHeightProperty = DependencyProperty.Register(
        nameof(FlareHeight), typeof(double), typeof(EdgeTabShape),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double FlareHeight
    {
        get => (double)GetValue(FlareHeightProperty);
        set => SetValue(FlareHeightProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        double f = FlareHeight;
        if (w <= 0 || h <= 2 * f) return;

        double r = Math.Min(CornerRadius, Math.Min(w / 2, (h - 2 * f) / 2));
        var corner = new Size(r, r);

        // Each flare leaves the screen edge vertically and lands horizontally where the convex corner starts,
        // so both joins are tangent-continuous.
        double reach = w - r;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(w, 0), true, true);
            ctx.BezierTo(new Point(w, f * 0.62), new Point(w - reach * 0.42, f), new Point(r, f), false, false);
            ctx.ArcTo(new Point(0, f + r), corner, 0, false, SweepDirection.Counterclockwise, false, false);
            ctx.LineTo(new Point(0, h - f - r), false, false);
            ctx.ArcTo(new Point(r, h - f), corner, 0, false, SweepDirection.Counterclockwise, false, false);
            ctx.BezierTo(new Point(w - reach * 0.42, h - f), new Point(w, h - f * 0.62), new Point(w, h), false, false);
        }
        geometry.Freeze();

        dc.DrawGeometry(Fill, null, geometry);
    }
}
