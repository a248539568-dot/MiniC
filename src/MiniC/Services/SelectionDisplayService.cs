using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>让文件全名只跟随唯一选中项展示。</summary>
public static class SelectionDisplayService
{
    public static void Update(IEnumerable<DesktopItem> items)
    {
        var snapshot = items.ToArray();
        var selected = snapshot.Where(item => item.IsSelected).Take(2).ToArray();
        var single = selected.Length == 1 ? selected[0] : null;
        foreach (var item in snapshot) item.ShowFullName = ReferenceEquals(item, single);
    }
}
