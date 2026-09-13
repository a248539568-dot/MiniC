using MiniC.Models;

namespace MiniC.Services;

/// <summary>修复旧布局中仍有项目归属、但收纳盒快照缺失的兼容状态。</summary>
public static class LayoutRepairService
{
    private const double DefaultWidth = 380;
    private const double DefaultHeight = 320;
    private const double HorizontalGap = 24;
    private const double VerticalGap = 24;
    private const double StartInsetX = 70;
    private const double StartInsetY = 130;

    /// <summary>为所有仍被归属记录引用的编号补建收纳盒，返回新增数量。</summary>
    public static int EnsureReferencedGroups(
        LayoutState layout,
        double virtualLeft,
        double virtualTop,
        double virtualWidth)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var knownIds = layout.Groups
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedIds = (layout.LegacyAssignments?.Values ?? Enumerable.Empty<string>())
            .Concat(layout.NativeDesktop.Assignments.Values)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !knownIds.Contains(id))
            .ToArray();
        if (referencedIds.Length == 0) return 0;

        var usableWidth = Math.Max(DefaultWidth, virtualWidth - StartInsetX * 2);
        var columns = Math.Max(1, (int)Math.Floor((usableWidth + HorizontalGap) / (DefaultWidth + HorizontalGap)));
        var occupiedCount = layout.Groups.Count;
        for (var index = 0; index < referencedIds.Length; index++)
        {
            var slot = occupiedCount + index;
            layout.Groups.Add(new GroupState
            {
                Id = referencedIds[index],
                Name = $"恢复收纳盒 {index + 1}",
                X = virtualLeft + StartInsetX + slot % columns * (DefaultWidth + HorizontalGap),
                Y = virtualTop + StartInsetY + slot / columns * (DefaultHeight + VerticalGap),
                Width = DefaultWidth,
                Height = DefaultHeight
            });
            knownIds.Add(referencedIds[index]);
        }
        return referencedIds.Length;
    }
}
