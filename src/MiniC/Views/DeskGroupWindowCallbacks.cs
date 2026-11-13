using MiniC.ViewModels;

namespace MiniC.Views;

/// <summary>独立收纳盒窗口向应用协调器提交业务动作的窄接口。</summary>
public sealed record DeskGroupWindowCallbacks(
    Action<DeskGroup> StateChanged,
    Func<DeskGroup, IReadOnlyList<string>, bool, Task> CollectPathsAsync,
    Func<DesktopItem, IReadOnlyList<string>, bool, Task> DropOnItemAsync,
    Func<DeskGroup, IReadOnlyList<string>, System.Windows.Point, System.Windows.DragDropEffects, Task> ReleasePathsAsync,
    Func<DeskGroup, DesktopItem, string, Task<bool>> RenameItemAsync,
    Func<DeskGroup, IReadOnlyList<DesktopItem>, DesktopItem, System.Drawing.Point, Task> ShowItemMenuAsync,
    Action<DeskGroup> InteractionStarted,
    Action<bool> SetDesktopDropTargetActive,
    Action<DeskGroup> CreateGroup,
    Action<DeskGroup> DissolveGroup,
    Action RestoreDesktopLayerPlacement)
{
    /// <summary>收纳盒空白区域菜单。旧回调调用方未提供时保持无操作。</summary>
    public Func<DeskGroup, System.Drawing.Point, Task> ShowBackgroundMenuAsync { get; init; } =
        static (_, _) => Task.CompletedTask;

    public Func<System.Windows.Rect, System.Windows.Rect>? SnapBounds { get; init; }
}
