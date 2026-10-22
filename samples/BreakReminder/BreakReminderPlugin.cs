using Notch.Core.Activities;
using Notch.Core.Plugins;
using Notch.Core.Widgets;

namespace BreakReminder;

/// <summary>
/// The sample plugin from docs/plugins.md. It shows a card counting down to the next break and,
/// when the time is up, an activity in the pill until the card is clicked. It also offers the same
/// countdown as a widget for the Home tab.
/// </summary>
public sealed class BreakReminderPlugin : INotchPlugin
{
    private const string ReminderId = "reminder";
    private const string IntervalKey = "intervalMinutes";
    private const string WidgetTypeId = "next-break";
    private const string LabelKey = "label";

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

        // A widget the user can put on Home from "Add widget". Each one placed is filled in by Update.
        host.Widgets.Register(new PluginWidgetType
        {
            Id = WidgetTypeId,
            Name = "Next break",
            Description = "Counts down to your next break",
            Glyph = "",
            Options = [new WidgetOption(LabelKey, "Label", WidgetOptionType.Text, "Next break")],
            Placed = _ => Tick(),
            OptionsChanged = _ => Tick(),
        });

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

        foreach (PluginWidget widget in host.Widgets.Placed)
        {
            host.Widgets.Show(widget.Id, new PluginWidgetContent
            {
                Title = widget.Option(LabelKey),
                Blocks =
                [
                    new PluginProgress
                    {
                        Label = due ? "Time for a break" : "Until your break",
                        Value = due ? "Now" : $"{Math.Ceiling(left.TotalMinutes)} min",
                        Progress = Math.Clamp(elapsed / _interval, 0, 1),
                        Color = due ? GlowColor.Violet : null,
                    },
                    new PluginButtons { Actions = [new PluginAction { Label = due ? "I'm back" : "Start over", Clicked = Restart }] },
                ],
            });
        }

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
