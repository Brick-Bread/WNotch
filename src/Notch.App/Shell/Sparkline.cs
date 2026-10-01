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
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d).ResetBrushes()));

    private readonly Queue<double> _values = new(Capacity);
    private Pen? _pen;
    private Brush? _fill;

    /// <summary>Colour of the line; the area under it is washed with the same colour.</summary>
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

        // The same points twice: closed down to the baseline for the wash, open for the line.
        double left = x;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext lineContext = line.Open())
        using (StreamGeometryContext areaContext = area.Open())
        {
            areaContext.BeginFigure(new Point(left, ActualHeight), isFilled: true, isClosed: true);
            bool first = true;
            foreach (double value in _values)
            {
                var point = new Point(x, top + ((1 - value) * height));
                areaContext.LineTo(point, isStroked: false, isSmoothJoin: true);
                if (first)
                {
                    lineContext.BeginFigure(point, isFilled: false, isClosed: false);
                    first = false;
                }
                else
                {
                    lineContext.LineTo(point, isStroked: true, isSmoothJoin: true);
                }

                x += step;
            }

            areaContext.LineTo(new Point(x - step, ActualHeight), isStroked: false, isSmoothJoin: false);
        }

        line.Freeze();
        area.Freeze();
        _pen ??= new Pen(Stroke, Thickness) { LineJoin = PenLineJoin.Round };
        _fill ??= CreateFill(Stroke);
        drawingContext.DrawGeometry(_fill, null, area);
        drawingContext.DrawGeometry(null, _pen, line);
    }

    private void ResetBrushes()
    {
        _pen = null;
        _fill = null;
    }

    /// <summary>The line's colour fading out towards the bottom; nothing for a stroke that is not one flat colour.</summary>
    private static Brush CreateFill(Brush stroke)
    {
        if (stroke is not SolidColorBrush { Color: var color })
        {
            return Brushes.Transparent;
        }

        var fill = new LinearGradientBrush(
            Color.FromArgb(0x55, color.R, color.G, color.B),
            Color.FromArgb(0x00, color.R, color.G, color.B),
            angle: 90);
        fill.Freeze();
        return fill;
    }
}
