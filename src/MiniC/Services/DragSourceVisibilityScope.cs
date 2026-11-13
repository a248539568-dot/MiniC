using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>浮动预览存续时隐藏源图标绘制但保留布局，取消和异常退出都恢复。</summary>
internal sealed class DragSourceVisibilityScope : IDisposable
{
    private readonly (DesktopItem Item, bool Previous)[] _items;
    public DragSourceVisibilityScope(IEnumerable<DesktopItem> items)
    {
        _items = items.Distinct().Select(item => (item, item.IsDragPreviewSource)).ToArray();
        foreach (var (item, _) in _items) item.IsDragPreviewSource = true;
    }
    public void Dispose()
    {
        foreach (var (item, previous) in _items) item.IsDragPreviewSource = previous;
    }
}
