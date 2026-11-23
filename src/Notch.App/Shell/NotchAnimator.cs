using System.Windows.Media;
using Notch.Core.Animation;

namespace Notch.App.Shell;

/// <summary>Drives the notch's width, height and corner radius with springs, one step per rendered frame.</summary>
internal sealed class NotchAnimator(double width, double height, double radius)
{
    // Growing, the island stretches sideways a beat before it drops down, with a soft overshoot, the way the
    // Dynamic Island opens. Shrinking, it pulls in quicker and settles without a wobble, height first.
    // (Response in seconds, damping ratio.)
    private static readonly (double Response, double Damping) GrowWidth = (0.34, 0.74);
    private static readonly (double Response, double Damping) GrowHeight = (0.42, 0.76);
    private static readonly (double Response, double Damping) ShrinkWidth = (0.34, 0.9);
    private static readonly (double Response, double Damping) ShrinkHeight = (0.28, 0.94);
    private static readonly (double Response, double Damping) Corners = (0.3, 1);

    private readonly Spring _width = new(width);
    private readonly Spring _height = new(height);
    private readonly Spring _radius = Tuned(new Spring(radius), Corners);
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
        bool grows = width * height > _width.Value * _height.Value;
        Tuned(_width, grows ? GrowWidth : ShrinkWidth);
        Tuned(_height, grows ? GrowHeight : ShrinkHeight);
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

    private static Spring Tuned(Spring spring, (double Response, double Damping) tuning)
    {
        spring.Tune(tuning.Response, tuning.Damping);
        return spring;
    }
}
