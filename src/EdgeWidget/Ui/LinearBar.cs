using System.Windows;
using System.Windows.Media;

namespace EdgeWidget.Ui;

/// Horizontal progress bar with fully rounded ends (radius = height / 2).
public sealed class LinearBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LinearBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LinearBar),
        new FrameworkPropertyMetadata(Palette.Green, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LinearBar),
        new FrameworkPropertyMetadata(Palette.Track, FrameworkPropertyMetadataOptions.AffectsRender));

    /// Progress between 0 and 1.
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 4);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        double radius = height / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        double fraction = Math.Clamp(Value, 0, 1);
        if (fraction > 0)
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(height, fraction * width), height), radius, radius);
    }
}
