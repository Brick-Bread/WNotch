namespace Notch.Core.Shell;

/// <summary>Works out whether a screen point is on the island from the display's geometry, independent of mouse events.</summary>
public static class IslandHitTest
{
    /// <summary>True when the point lies on the island, which hangs from the top-centre of its display.</summary>
    /// <param name="displayLeft">Left edge of the display, in physical pixels.</param>
    /// <param name="displayTop">Top edge of the display, in physical pixels.</param>
    /// <param name="displayWidth">Width of the display, in physical pixels.</param>
    /// <param name="scale">Physical pixels per device-independent pixel.</param>
    /// <param name="width">Island width, in DIPs.</param>
    /// <param name="height">Island height, in DIPs.</param>
    /// <param name="x">The point, in physical pixels.</param>
    /// <param name="y">The point, in physical pixels.</param>
    /// <param name="margin">Extra room around the sides and bottom, in DIPs, so a pointer resting on the edge does not waver.</param>
    public static bool Contains(
        int displayLeft, int displayTop, int displayWidth, double scale, double width, double height, int x, int y, double margin = 0)
    {
        double centre = displayLeft + (displayWidth / 2.0);
        double half = ((width / 2) + margin) * scale;
        double bottom = displayTop + ((height + margin) * scale);
        return x >= centre - half && x < centre + half && y >= displayTop && y < bottom;
    }
}
