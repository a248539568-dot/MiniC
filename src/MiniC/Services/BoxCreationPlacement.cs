using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MiniC.Services;

/// <summary>Prefer nearby free space; when the screen is full, minimize unavoidable overlap.</summary>
internal static class BoxCreationPlacement
{
    internal const double Gap = 12;

    internal static Point Find(Point preferred, Size size, Rect workArea, IEnumerable<Rect> occupied)
    {
        var neighbors = occupied.Where(IsValid).ToArray();
        var maxX = Math.Max(workArea.Left, workArea.Right - size.Width);
        var maxY = Math.Max(workArea.Top, workArea.Bottom - size.Height);
        var xs = new HashSet<double> { Math.Clamp(preferred.X, workArea.Left, maxX), workArea.Left, maxX };
        var ys = new HashSet<double> { Math.Clamp(preferred.Y, workArea.Top, maxY), workArea.Top, maxY };
        foreach (var other in neighbors)
        {
            xs.Add(Math.Clamp(other.Right + Gap, workArea.Left, maxX));
            xs.Add(Math.Clamp(other.Left - size.Width - Gap, workArea.Left, maxX));
            ys.Add(Math.Clamp(other.Bottom + Gap, workArea.Top, maxY));
            ys.Add(Math.Clamp(other.Top - size.Height - Gap, workArea.Top, maxY));
        }
        return (from x in xs from y in ys
                let bounds = new Rect(x, y, size.Width, size.Height)
                let overlap = neighbors.Sum(other => Overlap(bounds, other))
                let distance = (new Point(x, y) - preferred).LengthSquared
                orderby overlap, distance, y, x
                select new Point(x, y)).First();
    }

    private static double Overlap(Rect bounds, Rect other)
    {
        other.Inflate(Gap, Gap);
        var intersection = Rect.Intersect(bounds, other);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }

    private static bool IsValid(Rect bounds) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);
}
