using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using MiniC.ViewModels;
using Microsoft.Win32;

namespace MiniC.Services;

/// <summary>监视真实桌面目录，并提供桌面项目重命名与原生文件夹投放能力。</summary>
public sealed class DesktopFileService : IDisposable
{
    public const string RecycleBinShellPath = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ShellChangeNotificationService _shellNotifications = new();
    private readonly System.Timers.Timer _changeTimer;
    private readonly object _changeLock = new();
    private readonly List<DesktopRename> _pendingRenames = [];
    private bool _pendingFileChanges;
    private readonly string _userDesktop;
    private readonly string _publicDesktop;
    private bool _disposed;

    public DesktopFileService() : this(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)) { }

    internal DesktopFileService(string userDesktop, string publicDesktop)
    {
        _userDesktop = userDesktop;
        _publicDesktop = publicDesktop;
        _changeTimer = new System.Timers.Timer(280) { AutoReset = false };
        _changeTimer.Elapsed += (_, _) => RaisePendingChanges();
    }

    public event EventHandler<DesktopChangedEventArgs>? DesktopChanged;

    /// <summary>读取 Windows 桌面设置中当前启用显示的系统虚拟图标。</summary>
    public static IReadOnlyList<DesktopItem> GetVisibleVirtualDesktopItems()
    {
        var items = new List<DesktopItem>();
        AddVirtual(items, "{59031A47-3F72-44A7-89C5-5595FE6B30EE}", Environment.UserName, false);
        AddVirtual(items, "{645FF040-5081-101B-9F08-00AA002F954E}", "回收站", true);
        AddVirtual(items, "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑", false);
        AddVirtual(items, "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "网络", false);
        return items;
    }

    /// <summary>读取 Explorer 当前是否显示已知文件类型的扩展名。</summary>
    public static bool AreFileExtensionsVisible()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return Convert.ToInt32(key?.GetValue("HideFileExt", 1), CultureInfo.InvariantCulture) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static long GetRecycleBinItemCount()
    {
        var info = new ShellQueryRecycleBinInfo { Size = (uint)Marshal.SizeOf<ShellQueryRecycleBinInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.ItemCount : -1;
    }

    /// <summary>返回符合 Explorer 扩展名设置的原位重命名文本。</summary>
    public static string GetRenameEditName(DesktopItem item, bool? extensionsVisible = null)
    {
        if (item.IsDirectory) return item.Name;
        if (Path.GetExtension(item.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(item.Path);
        return extensionsVisible ?? AreFileExtensionsVisible()
            ? Path.GetFileName(item.Path)
            : Path.GetFileNameWithoutExtension(item.Path);
    }

    /// <summary>返回原位重命名时默认选中的文件名主体长度，不包含可见扩展名。</summary>
    public static int GetRenameSelectionLength(DesktopItem item, string editName)
    {
        if (item.IsDirectory) return editName.Length;
        var baseNameLength = editName.Length - Path.GetExtension(editName).Length;
        return baseNameLength > 0 ? baseNameLength : editName.Length;
    }

    /// <summary>把编辑文本解析为最终文件名；隐藏扩展名时保留原后缀。</summary>
    public static string GetRenameTargetFileName(
        string sourcePath,
        string normalizedName,
        bool isDirectory,
        bool extensionsVisible)
    {
        var sourceExtension = Path.GetExtension(sourcePath);
        var shortcut = sourceExtension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        var extension = isDirectory || (!shortcut && extensionsVisible) ? string.Empty : sourceExtension;
        return extension.Length > 0 && !normalizedName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? normalizedName + extension
            : normalizedName;
    }

    /// <summary>安全重命名真实桌面项目，不覆盖同名文件，并遵循 Explorer 扩展名设置。</summary>
    public Task<DesktopRenameResult> RenameItemAsync(
        string sourcePath,
        string displayName,
        bool? extensionsVisible = null) => Task.Run(() =>
    {
        try
        {
            var source = Path.GetFullPath(sourcePath);
            if (!PathExists(source) || !IsDesktopItem(source))
                return new DesktopRenameResult(false, null, "该项目不在真实桌面目录中。");

            var normalizedName = displayName.Trim();
            if (normalizedName.Length == 0)
                return new DesktopRenameResult(false, null, "名称不能为空。");
            if (normalizedName is "." or ".."
                || normalizedName.EndsWith(' ') || normalizedName.EndsWith('.')
                || normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return new DesktopRenameResult(false, null, "名称包含 Windows 不允许使用的字符或结尾。");

            var isDirectory = Directory.Exists(source);
            var fileName = GetRenameTargetFileName(
                source, normalizedName, isDirectory, extensionsVisible ?? AreFileExtensionsVisible());
            var destination = Path.Combine(Path.GetDirectoryName(source)!, fileName);
            if (source.Equals(destination, StringComparison.Ordinal))
                return new DesktopRenameResult(true, source, null);
            if (PathExists(destination) && !source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                return new DesktopRenameResult(false, null, $"“{fileName}”已经存在，请使用其他名称。");

            MovePathPreservingCaseChange(source, destination);
            NotifyDesktopChanged();
            return new DesktopRenameResult(true, destination, null);
        }
        catch (Exception ex)
        {
            return new DesktopRenameResult(false, null, ex.Message);
        }
    });

    /// <summary>把真实项目移动或复制到目标文件夹；同名时生成安全名称，不覆盖现有内容。</summary>
    public Task<DesktopFolderDropResult> DropIntoFolderAsync(
        IEnumerable<string> sourcePaths,
        string targetDirectory,
        bool copy) => Task.Run(() =>
    {
        var movedPaths = new List<string>();
        var destinationPaths = new List<string>();
        var errors = new List<string>();
        string target;
        try
        {
            target = Path.GetFullPath(targetDirectory);
            if (!Directory.Exists(target))
                return new DesktopFolderDropResult(movedPaths, destinationPaths, ["目标文件夹已经不存在。"]);
        }
        catch (Exception ex)
        {
            return new DesktopFolderDropResult(movedPaths, destinationPaths, [ex.Message]);
        }

        foreach (var sourceValue in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var source = Path.GetFullPath(sourceValue);
                if (!PathExists(source)) continue;
                if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"“{Path.GetFileName(source)}”不能放入自身。");
                    continue;
                }
                if (Directory.Exists(source) && IsPathInsideDirectory(target, source))
                {
                    errors.Add($"“{Path.GetFileName(source)}”不能放入它自己的子文件夹。");
                    continue;
                }

                var sourceParent = Path.GetDirectoryName(source);
                if (!copy && sourceParent?.Equals(target, StringComparison.OrdinalIgnoreCase) == true)
                {
                    destinationPaths.Add(source);
                    continue;
                }

                var destination = GetAvailablePath(target, Path.GetFileName(source));
                if (copy) CopyPath(source, destination);
                else
                {
                    MovePath(source, destination);
                    movedPaths.Add(source);
                }
                destinationPaths.Add(destination);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(sourceValue)}：{ex.Message}");
            }
        }
        NotifyDesktopChanged();
        return new DesktopFolderDropResult(movedPaths, destinationPaths, errors);
    });

    /// <summary>按 Windows 回收站语义删除真实文件，保留可恢复能力。</summary>
    public Task<DesktopRecycleResult> MoveToRecycleBinAsync(IEnumerable<string> sourcePaths) => Task.Run(() =>
    {
        var removed = new List<string>();
        var errors = new List<string>();
        foreach (var sourceValue in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var source = Path.GetFullPath(sourceValue);
                if (Directory.Exists(source))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        source,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else if (File.Exists(source))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        source,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else continue;
                removed.Add(source);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(sourceValue)}：{ex.Message}");
            }
        }
        NotifyDesktopChanged();
        SHChangeNotify(0x08000000, 0, null, null);
        return new DesktopRecycleResult(removed, errors);
    });

    public void StartWatching()
    {
        StopWatching();
        AddWatcher(_userDesktop);
        if (!_publicDesktop.Equals(_userDesktop, StringComparison.OrdinalIgnoreCase)) AddWatcher(_publicDesktop);
    }

    public bool StartWatching(IntPtr notificationWindow, uint notificationMessage)
    {
        // Photoshop 等程序直接写文件，不保证发送 Shell 通知，文件监听始终并行开启。
        StartWatching();
        if (_shellNotifications.Register(notificationWindow, notificationMessage,
                [_userDesktop, _publicDesktop, RecycleBinShellPath])) return true;
        MiniCLogger.Warning(nameof(DesktopFileService),
            "Shell change notification registration failed; file system watching remains active.");
        return false;
    }

    public void HandleShellChangeNotification(IntPtr changeHandle, IntPtr processId)
    {
        if (_shellNotifications.TryConsume(changeHandle, processId)) QueueChange();
    }

    private void AddWatcher(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                               | NotifyFilters.Size | NotifyFilters.Attributes
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            _watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // 公共桌面可能因策略暂时不可监视，主刷新入口仍可手动恢复。
            MiniCLogger.Warning(nameof(DesktopFileService), $"Unable to watch desktop directory: {path}");
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => QueueChange();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        MiniCLogger.Error(nameof(DesktopFileService), e.GetException(), "Desktop watcher error; schedule reconciliation.");
        QueueChange();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        lock (_changeLock)
        {
            if (_disposed) return;
            _pendingRenames.Add(new DesktopRename(e.OldFullPath, e.FullPath));
        }
        QueueChange(fileChange: false);
    }

    private void QueueChange(bool fileChange = true)
    {
        lock (_changeLock)
        {
            if (_disposed) return;
            _pendingFileChanges |= fileChange;
            _changeTimer.Stop();
            _changeTimer.Start();
        }
    }

    private void RaisePendingChanges()
    {
        DesktopRename[] renames;
        bool fileChanges;
        lock (_changeLock)
        {
            if (_disposed) return;
            renames = _pendingRenames.ToArray();
            _pendingRenames.Clear();
            fileChanges = _pendingFileChanges;
            _pendingFileChanges = false;
        }
        DesktopChanged?.Invoke(this, new DesktopChangedEventArgs(renames, fileChanges));
    }

    private static void AddVirtual(List<DesktopItem> items, string classId, string name, bool defaultVisible)
    {
        if (!IsVirtualVisible(classId, defaultVisible)) return;
        items.Add(new DesktopItem { Path = $"shell:::{classId}", Name = name, IsDirectory = true, IsVirtual = true });
    }

    private static bool IsVirtualVisible(string classId, bool defaultVisible)
    {
        const string keyRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";
        foreach (var subKey in new[] { "NewStartPanel", "ClassicStartMenu" })
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{keyRoot}\{subKey}");
            if (key?.GetValue(classId) is int hidden) return hidden == 0;
        }
        return defaultVisible;
    }

    private bool IsDesktopItem(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return parent is not null && (parent.Equals(_userDesktop, StringComparison.OrdinalIgnoreCase)
                                      || parent.Equals(_publicDesktop, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string GetAvailablePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, fileName);
        if (!PathExists(candidate)) return candidate;
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({index}){extension}");
            if (!PathExists(candidate)) return candidate;
        }
    }

    private static void MovePath(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void CopyPath(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            File.Copy(source, destination, overwrite: false);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: false);
        }
    }

    private static bool IsPathInsideDirectory(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void MovePathPreservingCaseChange(string source, string destination)
    {
        if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            MovePath(source, destination);
            return;
        }

        var directory = Path.GetDirectoryName(source)!;
        var temporary = Path.Combine(directory, $".MiniC-Rename-{Guid.NewGuid():N}.tmp");
        MovePath(source, temporary);
        try
        {
            MovePath(temporary, destination);
        }
        catch
        {
            if (PathExists(temporary) && !PathExists(source)) MovePath(temporary, source);
            throw;
        }
    }

    private void NotifyDesktopChanged()
    {
        NotifyShellPath(_userDesktop);
        NotifyShellPath(_publicDesktop);
    }

    private static void NotifyShellPath(string path)
    {
        try { SHChangeNotify(0x00001000, 0x0005, path, null); }
        catch
        {
            // Shell 正在重建时允许通知失败，文件监视器会在后续事件中刷新。
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string? path, string? secondPath);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ShellQueryRecycleBinInfo
    {
        public uint Size;
        public long TotalSize;
        public long ItemCount;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShellQueryRecycleBinInfo info);

    private void StopWatching()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _shellNotifications.Dispose();
    }

    public void Dispose()
    {
        lock (_changeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _changeTimer.Stop();
            _pendingRenames.Clear();
        }
        StopWatching();
        _changeTimer.Dispose();
    }
}
