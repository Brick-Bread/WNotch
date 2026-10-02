using Notch.Core.Activities;

namespace Notch.Core.Hud;

/// <summary>Turns readings of the Caps Lock key into a HUD each time it is switched on or off.</summary>
public sealed class CapsLockHudTracker
{
    private bool? _on;

    /// <summary>Feed every reading; returns the HUD to show, or null when the key is as it was.</summary>
    public Activity? Update(bool on)
    {
        bool? was = _on;
        _on = on;

        // The first reading only establishes the baseline.
        return was is null || was == on ? null : HudActivities.CapsLock(on);
    }
}
