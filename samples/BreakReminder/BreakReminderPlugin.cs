using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace BreakReminder;

/// <summary>
/// The sample plugin from docs/plugins.md. It shows a card counting down to the next break and,
/// when the time is up, an activity in the pill until the card is clicked.
/// </summary>
public sealed class BreakReminderPlugin : INotchPlugin
{
    private const string ReminderId = "reminder";
    private const string IntervalKey = "intervalMinutes";

    private readonly Lock _gate = new();
    private IPluginHost? _host;
    private Timer? _timer;
    private TimeSpan _interval;
    private DateTimeOffset _since;
    private bool _reminded;

    public void Start(IPluginHost host)
    {
        _host = host;

        // Written back so the option shows up in settings.json for the user to edit.
        int minutes = Math.Clamp(host.Settings.Get(IntervalKey, 50), 1, 600);
        host.Settings.Set(IntervalKey, minutes);
        _interval = TimeSpan.FromMinutes(minutes);
        _since = DateTimeOffset.UtcNow;

        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    public void Stop()
    {
        // Notch removes the card and the activity; only the plugin's own resources need releasing.
        _timer?.Dispose();
    }

    private void Tick()
    {
        // This runs on a timer thread, where an escaping exception would end the whole app.
        try
        {
            lock (_gate)
            {
                Update();
            }
        }
        catch (Exception e)
        {
            _host?.Log.Error("Could not update the break reminder.", e);
        }
    }

    private void Update()
    {
        IPluginHost host = _host!;
        TimeSpan elapsed = DateTimeOffset.UtcNow - _since;
        TimeSpan left = _interval - elapsed;
        bool due = left <= TimeSpan.Zero;

        host.Cards.Set(new PluginCard
        {
            Id = "countdown",
            Label = "Next break",
            Value = due ? "Now" : $"{Math.Ceiling(left.TotalMinutes)} min",
            Detail = due ? "Click when you are back" : "Click to start over",
            Progress = Math.Clamp(elapsed / _interval, 0, 1),
            Clicked = Restart,
        });

        if (due && !_reminded)
        {
            _reminded = true;
            host.Activities.Publish(new Activity
            {
                Id = ReminderId,
                Tier = ActivityTier.Attention,
                Title = "Take a break",
                Detail = $"{_interval.TotalMinutes:0} min",
                Glyph = "",
                Glow = new Glow(GlowColor.Violet, GlowPattern.Pulse),
            });
        }
    }

    private void Restart()
    {
        lock (_gate)
        {
            _since = DateTimeOffset.UtcNow;
            _reminded = false;
            _host!.Activities.Remove(ReminderId);
            Update();
        }
    }
}
