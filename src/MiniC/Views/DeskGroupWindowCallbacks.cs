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
    Func<DeskGroup, IReadOnlyList<DeskGroup>> GetMergeTargets,
    Func<DeskGroup, System.Windows.Point, DeskGroup?> FindMergeTarget,
    Action<DeskGroup, DeskGroup?> MergeDragTargetChanged,
    Action<DeskGroup, DeskGroup> MergeGroups,
    Action<DeskGroup, DeskGroup> MergeTabIntoGroup,
    Action<DeskGroup, System.Windows.Point> DetachGroup,
    Action<IReadOnlyList<DeskGroup>> TabOrderChanged);
