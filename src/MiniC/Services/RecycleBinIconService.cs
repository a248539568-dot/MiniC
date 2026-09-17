using System.Windows.Media;
using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>回收站图标的唯一写入路径；串行查询真实计数，不采用虚拟项目图标缓存。</summary>
internal sealed class RecycleBinIconService
{
    private readonly Func<Task<long>> _queryCount;
    private readonly Func<bool, Task<ImageSource?>> _loadIcon;
    private bool _refreshing;
    private bool _stopped;
    private bool? _isEmpty;
    private ImageSource? _icon;

    public RecycleBinIconService() : this(
        () => Task.Run(DesktopFileService.GetRecycleBinItemCount),
        isEmpty => Task.Run(() => ShellIconService.GetRecycleBinIcon(isEmpty))) { }

    internal RecycleBinIconService(Func<Task<long>> queryCount, Func<bool, Task<ImageSource?>> loadIcon)
    {
        _queryCount = queryCount;
        _loadIcon = loadIcon;
    }

    public static bool IsRecycleBin(DesktopItem item) => item.Path.Equals(
        DesktopFileService.RecycleBinShellPath, StringComparison.OrdinalIgnoreCase);

    // 从 UI 调度器调用；仅原生查询和图标获取在后台执行，避免阻塞点击/拖动。
    public async Task RefreshAsync(IReadOnlyCollection<DesktopItem> items)
    {
        if (_stopped || _refreshing || items.Count == 0) return;
        _refreshing = true;
        try
        {
            var count = await _queryCount();
            if (count < 0 || _stopped) return;
            var empty = count == 0;
            if (_icon is null || _isEmpty != empty)
            {
                var icon = await _loadIcon(empty);
                if (icon is null || _stopped) return;
                _icon = icon;
                _isEmpty = empty;
            }
            foreach (var item in items)
            {
                if (!IsRecycleBin(item)) continue;
                item.Icon = _icon;
                item.NeedsIconRefresh = false;
            }
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(RecycleBinIconService), exception, "Recycle Bin icon refresh failed; retry on next tick.");
        }
        finally { _refreshing = false; }
    }

    public void Stop() => _stopped = true;
}
