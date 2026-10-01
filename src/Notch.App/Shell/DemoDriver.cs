using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.Core.Hud;

namespace Notch.App.Shell;

/// <summary>Loops through fake activities (<c>--demo</c>) so every pill state can be seen without real system events.</summary>
internal sealed class DemoDriver : IDisposable
{
    private readonly ActivityManager _activities;
    private readonly DispatcherTimer _timer;
    private readonly Action[] _script;
    private int _step;

    public DemoDriver(ActivityManager activities)
    {
        _activities = activities;
        _script =
        [
            () => _activities.Publish(HudActivities.Volume(0.6, muted: false)),
            () => _activities.Publish(new Activity { Id = "agent", Tier = ActivityTier.Ongoing, Glyph = "", Title = "Claude", Detail = "Working" }),
            () => _activities.Publish(new Activity { Id = "agent", Tier = ActivityTier.Attention, Glyph = "", Title = "Claude", Detail = "Needs input" }),
            () => _activities.Publish(HudActivities.Power(pluggedIn: true, percent: 82)),
            () => _activities.Publish(HudActivities.Bluetooth("Headphones", connected: true)),
            () => _activities.Publish(HudActivities.Brightness(0.4)),
            () => _activities.Remove("agent"),
        ];

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => _script[_step++ % _script.Length]();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
