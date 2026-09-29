using System.Windows;
using System.Windows.Media;

namespace OpenControlEdge.Ui;

/// A monochrome icon from Icons (24×24 box) drawn at the element's size in the Foreground colour, so logos follow
/// the theme (white on the dark panel, dark on the light one).
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Drawing), typeof(IconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(IconView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Drawing? Icon
    {
        get => (Drawing?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Icon is not Drawing icon || ActualWidth <= 0 || ActualHeight <= 0) return;
        double size = Math.Min(ActualWidth, ActualHeight);
        Color color = Foreground is SolidColorBrush brush ? brush.Color : Colors.White;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 24, size / 24));
        dc.DrawDrawing(Icons.Tint(icon, color));
        dc.Pop();
        dc.Pop();
    }
}
