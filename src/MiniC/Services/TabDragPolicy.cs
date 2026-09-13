using System.Windows;

namespace MiniC.Services;

/// <summary>标签手势只在开始时选择方向：横向排序，纵向转移；手势中途不互相切换。</summary>
internal static class TabDragPolicy
{
    private const double StartDistance = 10;
    private const double DirectionBias = 1.15;

    public static TabDragMode ResolveMode(TabDragMode current, Vector displacement)
    {
        if (current != TabDragMode.Pending) return current;
        var x = Math.Abs(displacement.X);
        var y = Math.Abs(displacement.Y);
        if (Math.Max(x, y) < StartDistance) return TabDragMode.Pending;
        if (x >= y * DirectionBias) return TabDragMode.Reorder;
        if (y >= x * DirectionBias) return TabDragMode.Transfer;
        return TabDragMode.Pending;
    }

    public static TabDropIntent ResolveIntent(TabDragMode mode, bool insideSourceTabs, bool hasMergeTarget) =>
        mode != TabDragMode.Transfer || insideSourceTabs ? TabDropIntent.Reorder
        : hasMergeTarget ? TabDropIntent.Merge : TabDropIntent.Detach;
}

internal enum TabDragMode { None, Pending, Reorder, Transfer }
internal enum TabDropIntent { Reorder, Detach, Merge }
