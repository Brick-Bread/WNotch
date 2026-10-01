using Notch.Core.Settings;

namespace Notch.Core.Shell;

/// <summary>
/// Where the island is on its display and how it meets the screen edge, worked out from the
/// user's position and style settings. Sizes are in DIPs unless a parameter says otherwise.
/// </summary>
/// <param name="TaskbarHeight">Height of the taskbar on the display; only matters in the taskbar position.</param>
public readonly record struct IslandPlacement(NotchPosition Position, NotchStyle Style, double TaskbarHeight = IslandPlacement.DefaultTaskbarHeight)
{
    /// <summary>Used when the taskbar's height cannot be told, e.g. while it hides itself.</summary>
    public const double DefaultTaskbarHeight = 48;

    /// <summary>Height of the idle pill, which a floating island centres in the taskbar.</summary>
    public const double PillHeight = 32;

    /// <summary>How far a floating island keeps from the top edge.</summary>
    public const double TopGap = 8;

    /// <summary>How far the island keeps from the left edge in the taskbar position.</summary>
    public const double SideInset = 12;

    private const double SmallestBottomGap = 4;

    /// <summary>The island sits on the bottom edge and opens upwards, rather than hanging from the top.</summary>
    public bool AtBottom => Position == NotchPosition.TaskbarLeft;

    /// <summary>The island is rounded all the way around and keeps clear of the edge.</summary>
    public bool Floating => Style == NotchStyle.Island;

    /// <summary>Space between the screen edge and the island: none for a notch, enough to centre the pill in the taskbar there.</summary>
    public double EdgeGap => !Floating
        ? 0
        : AtBottom ? Math.Max(SmallestBottomGap, (TaskbarHeight - PillHeight) / 2) : TopGap;

    /// <summary>
    /// True when a screen point is on the island or in the gap between it and its edge, so that
    /// pushing the pointer against the edge still reaches a floating island.
    /// </summary>
    /// <param name="displayLeft">The display's bounds, in physical pixels.</param>
    /// <param name="displayTop">The display's bounds, in physical pixels.</param>
    /// <param name="displayRight">The display's bounds, in physical pixels.</param>
    /// <param name="displayBottom">The display's bounds, in physical pixels.</param>
    /// <param name="scale">Physical pixels per DIP.</param>
    /// <param name="width">The island's current width.</param>
    /// <param name="height">The island's current height.</param>
    /// <param name="x">The point, in physical pixels.</param>
    /// <param name="y">The point, in physical pixels.</param>
    /// <param name="margin">Extra room around the island so a pointer resting on its outline does not waver.</param>
    public bool Contains(
        int displayLeft, int displayTop, int displayRight, int displayBottom, double scale, double width, double height, int x, int y, double margin = 0)
    {
        double reach = (EdgeGap + height + margin) * scale;
        if (AtBottom)
        {
            double left = displayLeft + ((SideInset - margin) * scale);
            double right = displayLeft + ((SideInset + width + margin) * scale);
            return x >= left && x < right && y < displayBottom && y >= displayBottom - reach;
        }

        double centre = (displayLeft + displayRight) / 2.0;
        double half = ((width / 2) + margin) * scale;
        return x >= centre - half && x < centre + half && y >= displayTop && y < displayTop + reach;
    }
}
