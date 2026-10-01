using System.Windows;
using System.Windows.Media;

namespace Notch.App.Shell;

/// <summary>A small line chart of recent 0..1 values, newest at the right edge.</summary>
internal sealed class Sparkline : FrameworkElement
{
    private const int Capacity = 60;

    private const double Thickness = 1.5;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d)._pen = null));

    private readonly Queue<double> _values = new(Capacity);
    private Pen? _pen;

    /// <summary>Colour of the line.</summary>
    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public void Push(double value)
    {
        if (_values.Count == Capacity)
        {
            _values.Dequeue();
        }

        _values.Enqueue(Math.Clamp(value, 0, 1));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // Leave room for the stroke so a flat line at 0 or 1 is not clipped.
        double top = Thickness;
        double height = ActualHeight - (2 * Thickness);
        double step = ActualWidth / (Capacity - 1);
        double x = ActualWidth - ((_values.Count - 1) * step);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            bool first = true;
            foreach (double value in _values)
            {
                var point = new Point(x, top + ((1 - value) * height));
                if (first)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    first = false;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }

                x += step;
            }
        }

        geometry.Freeze();
        _pen ??= new Pen(Stroke, Thickness) { LineJoin = PenLineJoin.Round };
        drawingContext.DrawGeometry(null, _pen, geometry);
    }
}
