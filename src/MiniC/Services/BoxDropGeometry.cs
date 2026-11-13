using System.Windows;
using MiniC.Models;
using Point = System.Windows.Point;

namespace MiniC.Services;

internal static class BoxDropGeometry
{
    // The visible center overlay is the only merge target.
    public static Rect Center(Rect bounds)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return Rect.Empty;
        return new Rect(bounds.Left + bounds.Width * 0.2, bounds.Top + bounds.Height * 0.2,
            bounds.Width * 0.6, bounds.Height * 0.6);
    }

    public static bool Contains(Rect bounds, Point pointer) => Center(bounds).Contains(pointer);

    public static BoxTabRef? FindTarget(string sourceHostId, IEnumerable<BoxDropTarget> topToBottom,
        Point pointer, bool wholeBox = false)
    {
        foreach (var target in topToBottom)
        {
            if (target.HostId == sourceHostId || !target.Bounds.Contains(pointer)) continue;
            // A nearer box's edge must not merge into another box hidden beneath it.
            return (wholeBox || Contains(target.Bounds, pointer)) ? target.Tab : null;
        }
        return null;
    }
}

internal sealed record BoxDropTarget(string HostId, BoxTabRef Tab, Rect Bounds);
