using System.Windows.Media;
using Notch.Core.Animation;

namespace Notch.App.Shell;

/// <summary>Drives the notch's width, height and corner radius with springs, one step per rendered frame.</summary>
internal sealed class NotchAnimator(double width, double height, double radius)
{
    private readonly Spring _width = new(width);
    private readonly Spring _height = new(height);
    private readonly Spring _radius = new(radius);
    private TimeSpan? _lastFrame;
    private bool _running;

    public event Action? Frame;

    public double Width => _width.Value;

    public double Height => _height.Value;

    public double Radius => _radius.Value;

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
        }
    }
}
