using System.Windows;
using System.Windows.Media;

namespace Notch.App.Shell;

/// <summary>
/// The island's outline with continuous corners, the kind Apple draws: the curve eases out of the straight edge instead
/// of meeting it in a circular arc, which reads smoother at the large radius of the expanded notch. A corner too
/// large for the shape (a capsule, the slim resting bar) is a plain arc, as Apple's capsules are.
/// </summary>
internal static class IslandOutline
{
    // How far along each edge a continuous corner of radius r reaches, and the cubic Bézier control points that make
    // it up, as multiples of r (after the widely used measurements of the iOS corner).
    private const double Extent = 1.52866483;
    private const double C1 = 1.08849323;
    private const double C2 = 0.86840689;
    private const double P1 = 0.63149399;
    private const double Q1 = 0.07491100;
    private const double C3 = 0.37282392;
    private const double C4 = 0.16905899;

    /// <param name="roundTop">Round the top corners; a notch against the top of the screen has square ones there.</param>
    /// <param name="roundBottom">Round the bottom corners; an island in the taskbar sits square on the bottom edge.</param>
    public static Geometry Build(Rect rect, double radius, bool roundTop, bool roundBottom)
    {
        double half = Math.Max(0, Math.Min(rect.Width, rect.Height) / 2);
        radius = Math.Clamp(radius, 0, half);
        bool continuous = radius * Extent <= half && radius > 0.5;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            // Clockwise from the top-left corner's end. Each corner is its point and the directions of the edge
            // that comes into it and the edge that leaves it.
            Point topLeft = rect.TopLeft, topRight = rect.TopRight, bottomRight = rect.BottomRight, bottomLeft = rect.BottomLeft;
            var up = new Vector(0, -1);
            var right = new Vector(1, 0);
            var down = new Vector(0, 1);
            var left = new Vector(-1, 0);

            double reach = continuous ? radius * Extent : radius;
            context.BeginFigure(roundTop ? topLeft + (right * reach) : topLeft, isFilled: true, isClosed: true);
            Corner(context, topRight, right, down, roundTop ? radius : 0, continuous);
            Corner(context, bottomRight, down, left, roundBottom ? radius : 0, continuous);
            Corner(context, bottomLeft, left, up, roundBottom ? radius : 0, continuous);
            Corner(context, topLeft, up, right, roundTop ? radius : 0, continuous);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Draws the edge up to the corner at <paramref name="at"/>, then the corner, ending on the next edge.</summary>
    private static void Corner(StreamGeometryContext context, Point at, Vector incoming, Vector outgoing, double r, bool continuous)
    {
        // A point near the corner: s back along the incoming edge, t along the outgoing one.
        Point P(double s, double t) => at - (incoming * (s * r)) + (outgoing * (t * r));

        if (r <= 0)
        {
            context.LineTo(at, isStroked: true, isSmoothJoin: false);
            return;
        }

        if (!continuous)
        {
            context.LineTo(P(1, 0), isStroked: true, isSmoothJoin: false);
            context.ArcTo(P(0, 1), new Size(r, r), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            return;
        }

        context.LineTo(P(Extent, 0), isStroked: true, isSmoothJoin: false);
        context.BezierTo(P(C1, 0), P(C2, 0), P(P1, Q1), isStroked: true, isSmoothJoin: true);
        context.BezierTo(P(C3, C4), P(C4, C3), P(Q1, P1), isStroked: true, isSmoothJoin: true);
        context.BezierTo(P(0, C2), P(0, C1), P(0, Extent), isStroked: true, isSmoothJoin: true);
    }
}
