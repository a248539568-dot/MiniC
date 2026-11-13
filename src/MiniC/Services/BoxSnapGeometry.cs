using System.Windows;

namespace MiniC.Services;

/// <summary>Small edge snaps in physical screen coordinates, shared by every box host.</summary>
internal static class BoxSnapGeometry
{
    public const double Threshold = 6;
    private const double NeighborDistance = 32;

    public static Rect Snap(Rect moving, IEnumerable<Rect> neighbors, double dpiScale = 1)
    {
        if (!IsValid(moving)) return moving;
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        var threshold = Threshold * scale;
        var reach = NeighborDistance * scale;
        var dx = double.PositiveInfinity;
        var dy = double.PositiveInfinity;
        foreach (var target in neighbors)
        {
            if (!IsValid(target)) continue;
            if (IntervalDistance(moving.Top, moving.Bottom, target.Top, target.Bottom) <= reach)
            {
                Consider(ref dx, target.Left - moving.Left, threshold);
                Consider(ref dx, target.Right - moving.Right, threshold);
                Consider(ref dx, target.Left - moving.Right, threshold);
                Consider(ref dx, target.Right - moving.Left, threshold);
            }
            if (IntervalDistance(moving.Left, moving.Right, target.Left, target.Right) <= reach)
            {
                Consider(ref dy, target.Top - moving.Top, threshold);
                Consider(ref dy, target.Bottom - moving.Bottom, threshold);
                Consider(ref dy, target.Top - moving.Bottom, threshold);
                Consider(ref dy, target.Bottom - moving.Top, threshold);
            }
        }
        return new Rect(moving.X + (double.IsFinite(dx) ? dx : 0),
            moving.Y + (double.IsFinite(dy) ? dy : 0), moving.Width, moving.Height);
    }

    private static bool IsValid(Rect bounds) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
        && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);

    private static double IntervalDistance(double start, double end, double otherStart, double otherEnd) =>
        Math.Max(0, Math.Max(otherStart - end, start - otherEnd));

    private static void Consider(ref double best, double correction, double threshold)
    {
        var distance = Math.Abs(correction);
        if (distance <= threshold && (distance < Math.Abs(best)
            || (distance == Math.Abs(best) && correction < best))) best = correction;
    }
}
