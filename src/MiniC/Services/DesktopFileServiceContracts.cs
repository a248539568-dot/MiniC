namespace MiniC.Services;

/// <summary>桌面文件由外部程序执行的重命名记录。</summary>
public sealed record DesktopRename(string OldPath, string NewPath);

/// <summary>真实桌面项目重命名操作的结果；失败时原路径保持不变。</summary>
public sealed record DesktopRenameResult(bool Succeeded, string? NewPath, string? Error);

/// <summary>拖入真实文件夹后的结果；同时返回被移走的源路径和实际生成的目标路径。</summary>
public sealed record DesktopFolderDropResult(
    List<string> MovedPaths,
    List<string> DestinationPaths,
    List<string> Errors);

/// <summary>移入系统回收站后的结果。</summary>
public sealed record DesktopRecycleResult(List<string> RemovedPaths, List<string> Errors);

/// <summary>桌面目录发生变化时传递给界面层的事件数据。</summary>
public sealed class DesktopChangedEventArgs(IReadOnlyCollection<DesktopRename> renames, bool hasFileChanges = true) : EventArgs
{
    public IReadOnlyCollection<DesktopRename> Renames { get; } = renames;
    public bool HasFileChanges { get; } = hasFileChanges;
}
