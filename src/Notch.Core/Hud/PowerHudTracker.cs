using Notch.Core.Activities;

namespace Notch.Core.Hud;

/// <summary>Decides which power readings are worth a HUD: plugging in, unplugging, and crossing a low-battery mark.</summary>
public sealed class PowerHudTracker
{
    private static readonly int[] LowMarks = [20, 10, 5];

    private bool? _pluggedIn;
    private int _percent = 100;

    /// <summary>Feed every reading; returns the HUD to show, or null when nothing notable changed.</summary>
    public Activity? Update(bool hasBattery, bool pluggedIn, int percent)
    {
        if (!hasBattery)
        {
            _pluggedIn = null;
            return null;
        }

        bool? wasPluggedIn = _pluggedIn;
        int previousPercent = _percent;
        _pluggedIn = pluggedIn;
        _percent = percent;

        // The first reading only establishes the baseline.
        if (wasPluggedIn is null)
        {
            return null;
        }

        if (wasPluggedIn != pluggedIn)
        {
            return HudActivities.Power(pluggedIn, percent);
        }

        if (!pluggedIn && LowMarks.Any(mark => previousPercent > mark && percent <= mark))
        {
            return HudActivities.LowBattery(percent);
        }

        return null;
    }
}
