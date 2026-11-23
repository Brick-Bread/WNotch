namespace Notch.Core.Animation;

/// <summary>A damped spring on a single value. Slightly underdamped by default for a small overshoot.</summary>
public sealed class Spring
{
    private const double MaxStep = 1.0 / 240.0;
    private const double RestDistance = 0.05;
    private const double RestVelocity = 0.5;

    public Spring(double initial, double stiffness = 320, double damping = 28)
    {
        Value = initial;
        Target = initial;
        Stiffness = stiffness;
        Damping = damping;
    }

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public double Target { get; set; }

    /// <summary>May change while the spring moves, e.g. to open with a bounce and close without one; it keeps its velocity.</summary>
    public double Stiffness { get; set; }

    public double Damping { get; set; }

    /// <summary>
    /// Stiffness and damping from the way SwiftUI describes a spring: <paramref name="response"/> is roughly how long a
    /// move takes, in seconds, and <paramref name="dampingRatio"/> how much it bounces (1 none, lower more).
    /// </summary>
    public static (double Stiffness, double Damping) Tuning(double response, double dampingRatio)
    {
        response = Math.Max(0.01, response);
        double omega = 2 * Math.PI / response;
        return (omega * omega, 2 * dampingRatio * omega);
    }

    public void Tune(double response, double dampingRatio) => (Stiffness, Damping) = Tuning(response, dampingRatio);

    public bool IsSettled => Math.Abs(Target - Value) < RestDistance && Math.Abs(Velocity) < RestVelocity;

    public void Step(double seconds)
    {
        // Sub-step so a dropped frame cannot make the integration blow up.
        seconds = Math.Clamp(seconds, 0, 0.1);
        while (seconds > 0)
        {
            double dt = Math.Min(seconds, MaxStep);
            double acceleration = (Stiffness * (Target - Value)) - (Damping * Velocity);
            Velocity += acceleration * dt;
            Value += Velocity * dt;
            seconds -= dt;
        }

        if (IsSettled)
        {
            SnapToTarget();
        }
    }

    public void SnapToTarget()
    {
        Value = Target;
        Velocity = 0;
    }
}
