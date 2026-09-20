using System.Windows;
using System.Windows.Media;

namespace EdgeWidget.Ui;

/// Detail-card background: a rounded rectangle with a triangular beak on its right edge.
/// The body occupies ActualWidth - BeakLength; the beak tip touches the right edge of the element.
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
        double w = ActualWidth - beak;
        double h = ActualHeight;
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
                ctx.LineTo(new Point(w, cy - half), false, false);
                ctx.LineTo(new Point(w + beak - 1.6, cy - 1.4), false, false);
                ctx.QuadraticBezierTo(new Point(w + beak, cy), new Point(w + beak - 1.6, cy + 1.4), false, false);
                ctx.LineTo(new Point(w, cy + half), false, false);
            }
            ctx.LineTo(new Point(w, h - r), false, false);
            ctx.ArcTo(new Point(w - r, h), corner, 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r, h), false, false);
            ctx.ArcTo(new Point(0, h - r), corner, 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(0, r), false, false);
            ctx.ArcTo(new Point(r, 0), corner, 0, false, SweepDirection.Clockwise, false, false);
        }
        geometry.Freeze();

        dc.DrawGeometry(Fill, null, geometry);
    }
}
