using System.Windows.Media;
using Notch.Core.Animation;

namespace Notch.App.Shell;

/// <summary>Drives the notch's width, height and corner radius with springs, one step per rendered frame.</summary>
internal sealed class NotchAnimator(double width, double height, double radius)
{
    // Size springs a little stiffer and looser than the default, for a quick move with a visible overshoot
    // (damping ratio about 0.67); the corners follow more calmly so they never look pinched.
    private const double SizeStiffness = 380;
    private const double SizeDamping = 26;
    private const double RadiusStiffness = 300;
    private const double RadiusDamping = 30;

    private readonly Spring _width = new(width, SizeStiffness, SizeDamping);
    private readonly Spring _height = new(height, SizeStiffness, SizeDamping);
    private readonly Spring _radius = new(radius, RadiusStiffness, RadiusDamping);
    private TimeSpan? _lastFrame;
    private bool _running;

    public event Action? Frame;

    /// <summary>Raised once the shape has come to rest.</summary>
    public event Action? Settled;

    public double Width => _width.Value;

    public double Height => _height.Value;

    public double Radius => _radius.Value;

    /// <summary>True while the shape is still moving towards its target.</summary>
    public bool IsRunning => _running;

    public void AnimateTo(double width, double height, double radius)
    {
        _width.Target = width;
        _height.Target = height;
        _radius.Target = radius;

        if (!_running)
        {
            _running = true;
            _lastFrame = null;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame is not { } last)
        {
            _lastFrame = now;
            return;
        }

        // Rendering can fire more than once per frame with the same timestamp.
        double seconds = (now - last).TotalSeconds;
        if (seconds <= 0)
        {
            return;
        }

        _lastFrame = now;
        _width.Step(seconds);
        _height.Step(seconds);
        _radius.Step(seconds);
        Frame?.Invoke();

        if (_width.IsSettled && _height.IsSettled && _radius.IsSettled)
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            Settled?.Invoke();
        }
    }
}
