using Notch.Core.Activities;

namespace Notch.Core.Shell;

public enum NotchMode
{
    /// <summary>Plain pill, nothing to show.</summary>
    Idle,

    /// <summary>Wider pill showing the top ongoing or attention activity.</summary>
    Compact,

    /// <summary>Briefly enlarged pill for a transient HUD.</summary>
    Peek,

    /// <summary>Full panel, opened by the user.</summary>
    Expanded,
}

public static class NotchModeResolver
{
    /// <param name="expanded">The user has the notch open (hover or click).</param>
    /// <param name="activities">Ordered as returned by <see cref="ActivityManager.Snapshot"/>.</param>
    public static NotchMode Resolve(bool expanded, IReadOnlyList<Activity> activities)
    {
        if (expanded)
        {
            return NotchMode.Expanded;
        }

        if (activities.Count == 0)
        {
            return NotchMode.Idle;
        }

        return activities[0].Tier == ActivityTier.Transient ? NotchMode.Peek : NotchMode.Compact;
    }
}
