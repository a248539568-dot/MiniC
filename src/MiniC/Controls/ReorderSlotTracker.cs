using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace MiniC.Controls;

/// <summary>使用排版完成后的格子命中，排除让位 RenderTransform，并在格子边界保留滞回。</summary>
internal sealed class ReorderSlotTracker
{
    private int? _lastIndex;
    public void Reset() => _lastIndex = null;

    internal static Rect LayoutBounds(FrameworkElement tile, Visual ancestor)
    {
        var offset = new Vector();
        DependencyObject? current = tile;
        while (current is Visual visual && !ReferenceEquals(current, ancestor))
        {
            offset += VisualTreeHelper.GetOffset(visual);
            current = VisualTreeHelper.GetParent(current);
        }
        return ReferenceEquals(current, ancestor) ? new Rect((Point)offset, tile.RenderSize) : Rect.Empty;
    }

    public int Select(IReadOnlyList<(int Index, Rect Bounds)> entries, Point pointer, bool list)
    {
        if (entries.Count == 0) return -1;
        double Distance(Rect bounds)
        {
            var dy = bounds.Top + bounds.Height / 2 - pointer.Y;
            var dx = list ? 0 : bounds.Left + bounds.Width / 2 - pointer.X;
            return Math.Sqrt(dx * dx + dy * dy);
        }
        var nearest = entries.MinBy(entry => Distance(entry.Bounds));
        if (_lastIndex is { } last)
        {
            var previous = entries.FirstOrDefault(entry => entry.Index == last);
            if (previous.Bounds.Width > 0 && Distance(previous.Bounds) <= Distance(nearest.Bounds) + 10)
                return last;
        }
        _lastIndex = nearest.Index;
        return nearest.Index;
    }
}
