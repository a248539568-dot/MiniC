using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>维护系统文件剪贴板快照，并同步桌面与收纳盒项目的复制/剪切视觉状态。</summary>
public sealed class DesktopClipboardCoordinator
{
    private ClipboardItemSnapshot _snapshot = ClipboardItemSnapshot.Empty;

    public void Refresh(IEnumerable<DesktopItem> desktopItems, IEnumerable<DesktopItem> groupedItems)
    {
        _snapshot = ClipboardItemStateService.ReadSnapshot();
        Apply(desktopItems, groupedItems);
    }

    public void Set(IEnumerable<string> paths, ClipboardTransferState state,
        IEnumerable<DesktopItem> desktopItems, IEnumerable<DesktopItem> groupedItems)
    {
        _snapshot = new ClipboardItemSnapshot(
            paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), state);
        Apply(desktopItems, groupedItems);
    }

    public void Apply(IEnumerable<DesktopItem> desktopItems, IEnumerable<DesktopItem> groupedItems)
    {
        var paths = _snapshot.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in desktopItems.Concat(groupedItems))
            item.ClipboardTransferState = paths.Contains(item.Path)
                ? _snapshot.State
                : ClipboardTransferState.None;
    }
}
