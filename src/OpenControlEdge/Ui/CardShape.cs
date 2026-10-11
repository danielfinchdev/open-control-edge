using System.Windows;
using System.Windows.Media;
using OpenControlEdge.Services;

namespace OpenControlEdge.Ui;

/// Detail-card background: a rounded rectangle with a triangular beak (concave sides) on its right edge.
/// The body occupies ActualWidth - BeakLength; the beak tip touches the right edge of the element. Side puts the beak on
/// another side (the one facing the panel): Left mirrored; Top and Bottom with BeakCenter running along the width.
public sealed class CardShape : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(CardShape),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(CardShape),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BeakLengthProperty = DependencyProperty.Register(
        nameof(BeakLength), typeof(double), typeof(CardShape),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BeakHalfHeightProperty = DependencyProperty.Register(
        nameof(BeakHalfHeight), typeof(double), typeof(CardShape),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// Vertical position of the beak tip, relative to the top of the card.
    public static readonly DependencyProperty BeakCenterProperty = DependencyProperty.Register(
        nameof(BeakCenter), typeof(double), typeof(CardShape),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SideProperty = DependencyProperty.Register(
        nameof(Side), typeof(PanelEdge), typeof(CardShape),
        new FrameworkPropertyMetadata(PanelEdge.Right, FrameworkPropertyMetadataOptions.AffectsRender));

    public PanelEdge Side
    {
        get => (PanelEdge)GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

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

    public double BeakLength
    {
        get => (double)GetValue(BeakLengthProperty);
        set => SetValue(BeakLengthProperty, value);
    }

    public double BeakHalfHeight
    {
        get => (double)GetValue(BeakHalfHeightProperty);
        set => SetValue(BeakHalfHeightProperty, value);
    }

    public double BeakCenter
    {
        get => (double)GetValue(BeakCenterProperty);
        set => SetValue(BeakCenterProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double beak = BeakLength;
        bool horizontal = Side is PanelEdge.Top or PanelEdge.Bottom;
        double w = (horizontal ? ActualHeight : ActualWidth) - beak;
        double h = horizontal ? ActualWidth : ActualHeight;
        if (w <= 0 || h <= 0) return;

        double r = Math.Min(CornerRadius, Math.Min(w, h) / 2);
        double half = BeakHalfHeight;
        bool hasBeak = h - 2 * r >= 2 * half;
        double cy = hasBeak ? Math.Min(Math.Max(BeakCenter, r + half), h - r - half) : 0;
        var corner = new Size(r, r);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(r, 0), true, true);
            ctx.LineTo(new Point(w - r, 0), false, false);
            ctx.ArcTo(new Point(w, r), corner, 0, false, SweepDirection.Clockwise, false, false);
            if (hasBeak)
            {
                // Slightly concave sides that meet in a softly rounded tip.
                ctx.LineTo(new Point(w, cy - half), false, false);
                ctx.QuadraticBezierTo(new Point(w + beak * 0.35, cy - half * 0.5), new Point(w + beak - 1.4, cy - 1.0), false, false);
                ctx.QuadraticBezierTo(new Point(w + beak, cy), new Point(w + beak - 1.4, cy + 1.0), false, false);
                ctx.QuadraticBezierTo(new Point(w + beak * 0.35, cy + half * 0.5), new Point(w, cy + half), false, false);
            }
            ctx.LineTo(new Point(w, h - r), false, false);
            ctx.ArcTo(new Point(w - r, h), corner, 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r, h), false, false);
            ctx.ArcTo(new Point(0, h - r), corner, 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(0, r), false, false);
            ctx.ArcTo(new Point(r, 0), corner, 0, false, SweepDirection.Clockwise, false, false);
        }
        geometry.Transform = new MatrixTransform(ToEdge(Side, ActualWidth, ActualHeight));
        geometry.Freeze();

        dc.DrawGeometry(Fill, null, geometry);
    }

    /// From the shape drawn for the right edge (the edge on the right, "along" running top to bottom) to the given edge
    /// in a box of width × height. For Top and Bottom the drawing is made in a height × width box (along = width).
    internal static Matrix ToEdge(PanelEdge edge, double width, double height) => edge switch
    {
        PanelEdge.Left => new Matrix(-1, 0, 0, 1, width, 0),
        PanelEdge.Bottom => new Matrix(0, 1, 1, 0, 0, 0),
        PanelEdge.Top => new Matrix(0, -1, 1, 0, 0, height),
        _ => Matrix.Identity,
    };
}
