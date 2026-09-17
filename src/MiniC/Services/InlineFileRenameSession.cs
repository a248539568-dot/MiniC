using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>共享的文件名提交状态：失焦、点击与失活只提交一次，Escape 只取消未提交的编辑。</summary>
internal sealed class InlineFileRenameSession
{
    private readonly Dictionary<DesktopItem, Task<bool>> _pending = [];

    public bool IsCommitting(DesktopItem item) => _pending.ContainsKey(item);

    public bool Cancel(DesktopItem item)
    {
        if (IsCommitting(item)) return false;
        item.IsRenaming = false;
        item.EditName = DesktopFileService.GetRenameEditName(item);
        return true;
    }

    public Task<bool> CommitAsync(DesktopItem item, Func<string, Task<bool>> rename, bool keepEditingOnFailure)
    {
        if (_pending.TryGetValue(item, out var pending)) return pending;
        if (!item.IsRenaming) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Add(item, completion.Task);
        var name = item.EditName;
        _ = CommitCoreAsync(item, name, rename, keepEditingOnFailure, completion);
        return completion.Task;
    }

    private async Task CommitCoreAsync(DesktopItem item, string name, Func<string, Task<bool>> rename,
        bool keepEditingOnFailure, TaskCompletionSource<bool> completion)
    {
        try
        {
            var succeeded = name.Equals(DesktopFileService.GetRenameEditName(item), StringComparison.Ordinal)
                            || await rename(name);
            if (succeeded || !keepEditingOnFailure)
            {
                item.IsRenaming = false;
                if (!succeeded) item.EditName = DesktopFileService.GetRenameEditName(item);
            }
            completion.TrySetResult(succeeded);
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(InlineFileRenameSession), exception, "Inline file rename failed.");
            item.IsRenaming = false;
            item.EditName = DesktopFileService.GetRenameEditName(item);
            completion.TrySetResult(false);
        }
        finally { _pending.Remove(item); }
    }
}
