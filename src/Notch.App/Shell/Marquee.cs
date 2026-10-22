using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Notch.App.Shell;

/// <summary>
/// Shows one line of text that may be wider than the room it has. When it does not fit, it holds still for a moment,
/// scrolls until its end is in view, holds again and scrolls back, over and over. The edges fade out while it
/// overflows. Text that fits stays still.
/// </summary>
internal sealed class Marquee : Decorator
{
    private const double Speed = 30; // DIPs a second
    private const double Hold = 1.4; // seconds at each end
    private const double Fade = 10; // DIPs of fade at an edge

    private readonly TranslateTransform _shift = new();
    private double _overflow = -1;

    public Marquee()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }

        // The child measures as wide as its text wants; the marquee takes no more than it is given.
        Child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        Size wanted = Child.DesiredSize;
        return new Size(Math.Min(wanted.Width, constraint.Width), wanted.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null)
        {
            return arrangeSize;
        }

        Child.RenderTransform = _shift;
        Child.Arrange(new Rect(0, 0, Math.Max(Child.DesiredSize.Width, arrangeSize.Width), arrangeSize.Height));
        Restart(Math.Max(0, Child.DesiredSize.Width - arrangeSize.Width), arrangeSize.Width);
        return arrangeSize;
    }

    private void Restart(double overflow, double width)
    {
        // Arrange runs often; only a change in how far there is to go starts the scroll again.
        if (Math.Abs(overflow - _overflow) < 0.5)
        {
            return;
        }

        _overflow = overflow;
        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = 0;

        if (overflow < 1 || width <= 0)
        {
            OpacityMask = null;
            return;
        }

        double travel = overflow / Speed;
        var there = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Hold + travel));
        var back = KeyTime.FromTimeSpan(TimeSpan.FromSeconds((2 * Hold) + (2 * travel)));
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var scroll = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds((3 * Hold) + (2 * travel)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        scroll.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Hold))));
        scroll.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow, there, ease));
        scroll.KeyFrames.Add(new DiscreteDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds((2 * Hold) + travel))));
        scroll.KeyFrames.Add(new EasingDoubleKeyFrame(0, back, ease));
        _shift.BeginAnimation(TranslateTransform.XProperty, scroll);

        double edge = Math.Min(0.25, Fade / width);
        OpacityMask = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Colors.Transparent, 0),
                new(Colors.Black, edge),
                new(Colors.Black, 1 - edge),
                new(Colors.Transparent, 1),
            },
            new Point(0, 0),
            new Point(1, 0));
    }
}
