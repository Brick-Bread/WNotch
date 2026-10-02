using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.Core.Agents;
using Notch.Core.Hud;
using Notch.Core.Terminal;

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
            () => _activities.Publish(AgentActivities.For("demo", "Claude", "", AgentState.Working, "notch")!),
            () => _activities.Publish(AgentActivities.For("demo", "Claude", "", AgentState.NeedsInput, "notch")!),
            () => _activities.Publish(AgentActivities.For("demo", "Claude", "", AgentState.Done, "notch")!),
            () => _activities.Publish(HudActivities.Power(pluggedIn: true, percent: 82)),
            () => _activities.Publish(HudActivities.Bluetooth("Headphones", connected: true)),
            () => _activities.Publish(HudActivities.Brightness(0.4)),
            () => _activities.Publish(HudActivities.CapsLock(on: true)),

            // Two sessions at once: each says there is another.
            () =>
            {
                _activities.Publish(Agent("demo", TerminalProfile.Claude, AgentState.Working, "notch"));
                _activities.Publish(Agent("demo2", TerminalProfile.Codex, AgentState.NeedsInput, "website"));
            },
            () =>
            {
                _activities.Remove(AgentActivities.IdFor("demo"));
                _activities.Remove(AgentActivities.IdFor("demo2"));
            },
            () => _activities.Publish(HudActivities.LowBattery(9)),
        ];

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => _script[_step++ % _script.Length]();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    private static Activity Agent(string session, TerminalProfile profile, AgentState state, string folder) =>
        AgentActivities.For(session, profile.DisplayName, profile.Glyph, state, folder, others: 1)!;
}
