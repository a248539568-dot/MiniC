using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>同步文件元数据时保留界面对象，避免重建图标容器打断点击、选择与编辑。</summary>
public static class DesktopItemRefreshService
{
    public static DesktopItem Merge(DesktopItem current, DesktopItem discovered)
    {
        if (current.IsDirectory != discovered.IsDirectory || current.IsVirtual != discovered.IsVirtual)
            return discovered;
        current.Name = discovered.Name;
        if (current.LastWriteTicks != discovered.LastWriteTicks)
        {
            current.LastWriteTicks = discovered.LastWriteTicks;
            current.NeedsIconRefresh = true;
        }
        return current;
    }
}
