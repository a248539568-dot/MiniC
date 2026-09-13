using MiniC.ViewModels;

namespace MiniC.Views;

/// <summary>桌面图标层向协调器提交的业务动作。</summary>
public sealed record DesktopSurfaceWindowCallbacks(
    Func<IReadOnlyList<string>, System.Windows.Point, System.Windows.DragDropEffects, Task> DragCompletedAsync,
    Func<IReadOnlyList<string>, System.Windows.Point, System.Windows.DragDropEffects, Task> ExternalDropAsync,
    Func<DesktopItem, IReadOnlyList<string>, bool, Task> DropOnItemAsync,
    Func<DesktopItem, string, Task<bool>> RenameItemAsync,
    Func<IReadOnlyList<DesktopItem>, DesktopItem, System.Drawing.Point, Task> ShowItemMenuAsync,
    Action InteractionStarted,
    Func<System.Drawing.Point, Task> ShowBackgroundMenuAsync);
