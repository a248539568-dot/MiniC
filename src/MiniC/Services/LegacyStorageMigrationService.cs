using System.IO;
using MiniC.Models;

namespace MiniC.Services;

/// <summary>把旧文件夹收纳版本遗留的真实项目一次性安全迁回桌面，并转换为显示层视觉归属。</summary>
public sealed class LegacyStorageMigrationService
{
    private readonly string _userDesktop;
    private readonly string _publicDesktop;
    private readonly string _storageRoot;
    private readonly string _veryOldStorageRoot;

    public LegacyStorageMigrationService()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest", "Storage"))
    {
    }

    internal LegacyStorageMigrationService(string userDesktop, string publicDesktop, string storageRoot)
    {
        _userDesktop = userDesktop;
        _publicDesktop = publicDesktop;
        _storageRoot = storageRoot;
        _veryOldStorageRoot = Path.Combine(userDesktop, ".DeskNest");
    }

    /// <summary>执行旧数据迁移。失败项保持原位，成功项才写入完整桌面路径归属。</summary>
    public Task<LegacyStorageMigrationResult> MigrateAsync(LayoutState layout) => Task.Run(() => Migrate(layout));

    /// <summary>在临时目录中验证旧 Storage 的安全迁移，不接触真实桌面和用户布局。</summary>
    internal static void ValidateIsolatedMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MiniC-LegacyMigration-{Guid.NewGuid():N}");
        var desktop = Path.Combine(root, "Desktop");
        var publicDesktop = Path.Combine(root, "PublicDesktop");
        var storage = Path.Combine(root, "Storage");
        var groupStorage = Path.Combine(storage, "group-a");
        try
        {
            Directory.CreateDirectory(desktop);
            Directory.CreateDirectory(publicDesktop);
            Directory.CreateDirectory(groupStorage);
            File.WriteAllText(Path.Combine(desktop, "report.txt"), "existing");
            File.WriteAllText(Path.Combine(groupStorage, "report.txt"), "stored");
            var layout = new LayoutState
            {
                Groups = [new GroupState { Id = "group-a", ItemOrder = ["report.txt"] }],
                LegacyAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["report.txt"] = "group-a"
                }
            };

            var result = new LegacyStorageMigrationService(desktop, publicDesktop, storage).Migrate(layout);
            var migratedPath = Path.Combine(desktop, "report (2).txt");
            if (result.Errors.Count != 0
                || result.MovedCount != 1
                || File.ReadAllText(Path.Combine(desktop, "report.txt")) != "existing"
                || File.ReadAllText(migratedPath) != "stored"
                || layout.NativeDesktop.Assignments.GetValueOrDefault(migratedPath) != "group-a"
                || layout.Groups[0].ItemOrder.Single() != "report (2).txt"
                || layout.LegacyAssignments is not null
                || Directory.Exists(storage))
                throw new InvalidOperationException("旧文件夹收纳迁移自检失败。");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch
            {
                // 自检临时目录清理失败不影响用户数据，系统临时目录可在后续自动清理。
            }
        }
    }

    private LegacyStorageMigrationResult Migrate(LayoutState layout)
    {
        var errors = new List<string>();
        var movedCount = 0;
        var groupDirectories = SafeDirectories(_storageRoot).ToArray();
        foreach (var groupDirectory in groupDirectories)
        {
            var groupId = Path.GetFileName(groupDirectory);
            movedCount += MoveEntries(layout, groupDirectory, groupId, errors);
            TryDeleteEmpty(groupDirectory);
        }

        foreach (var sourcePath in SafeEntries(_storageRoot)
                     .Where(path => !groupDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            var groupId = FindLegacyGroup(layout, Path.GetFileName(sourcePath));
            movedCount += MoveEntry(layout, sourcePath, groupId, errors);
        }
        TryDeleteEmpty(_storageRoot);

        foreach (var sourcePath in SafeEntries(_veryOldStorageRoot).ToArray())
        {
            var groupId = FindLegacyGroup(layout, Path.GetFileName(sourcePath));
            movedCount += MoveEntry(layout, sourcePath, groupId, errors);
        }
        TryDeleteEmpty(_veryOldStorageRoot);

        foreach (var (fileName, groupId) in layout.LegacyAssignments
                     ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            var desktopPath = ResolveDesktopPath(fileName);
            if (desktopPath is not null) layout.NativeDesktop.Assignments[desktopPath] = groupId;
        }

        if (errors.Count == 0 && !SafeEntries(_storageRoot).Any() && !SafeEntries(_veryOldStorageRoot).Any())
            layout.LegacyAssignments = null;
        return new LegacyStorageMigrationResult(movedCount, errors);
    }

    private int MoveEntries(LayoutState layout, string directory, string groupId, ICollection<string> errors)
    {
        var movedCount = 0;
        foreach (var sourcePath in SafeEntries(directory).ToArray())
            movedCount += MoveEntry(layout, sourcePath, groupId, errors);
        return movedCount;
    }

    private int MoveEntry(LayoutState layout, string sourcePath, string? groupId, ICollection<string> errors)
    {
        var originalName = Path.GetFileName(sourcePath);
        try
        {
            var destination = GetAvailablePath(_userDesktop, originalName);
            MovePath(sourcePath, destination);
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                layout.NativeDesktop.Assignments[destination] = groupId;
                UpdateItemOrder(layout, groupId, originalName, Path.GetFileName(destination));
            }
            return 1;
        }
        catch (Exception ex)
        {
            errors.Add($"{originalName}：{ex.Message}");
            return 0;
        }
    }

    private string? ResolveDesktopPath(string fileName)
    {
        if (!Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal)) return null;
        var userPath = Path.Combine(_userDesktop, fileName);
        if (PathExists(userPath)) return userPath;
        var publicPath = Path.Combine(_publicDesktop, fileName);
        return PathExists(publicPath) ? publicPath : null;
    }

    private static string? FindLegacyGroup(LayoutState layout, string fileName) =>
        layout.LegacyAssignments?.TryGetValue(fileName, out var groupId) == true ? groupId : null;

    private static void UpdateItemOrder(LayoutState layout, string groupId, string oldName, string newName)
    {
        if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) return;
        var group = layout.Groups.FirstOrDefault(candidate =>
            candidate.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        if (group is null) return;
        var index = group.ItemOrder.FindIndex(item => item.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) group.ItemOrder[index] = newName;
    }

    private static IEnumerable<string> SafeEntries(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.EnumerateFileSystemEntries(directory).ToArray() : []; }
        catch { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).ToArray() : []; }
        catch { return []; }
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

    private static void TryDeleteEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch
        {
            // 迁移完成后清理空目录失败不影响文件安全，下次启动会再次尝试。
        }
    }
}

/// <summary>旧文件夹收纳数据迁移结果。</summary>
public sealed record LegacyStorageMigrationResult(int MovedCount, IReadOnlyList<string> Errors);
