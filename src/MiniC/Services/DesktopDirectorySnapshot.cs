using System.IO;

namespace MiniC.Services;

/// <summary>桌面直接子项目的轻量元数据快照，不读取缩略图或 Shell 图标。</summary>
internal sealed class DesktopDirectorySnapshot
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Paths => _entries.Keys;

    public static DesktopDirectorySnapshot Capture(IEnumerable<string> paths)
    {
        var snapshot = new DesktopDirectorySnapshot();
        foreach (var path in paths)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
                    ? new DirectoryInfo(path) : new FileInfo(path);
                snapshot._entries[path] = new Entry(info.LastWriteTimeUtc.Ticks,
                    info is FileInfo file ? file.Length : -1, attributes);
            }
            catch (IOException)
            {
                // 保存或原子替换期间不可读的项目保留路径，后续可读时会产生差异。
                snapshot._entries[path] = default;
            }
            catch (UnauthorizedAccessException)
            {
                snapshot._entries[path] = default;
            }
        }
        return snapshot;
    }

    public bool Matches(DesktopDirectorySnapshot? other) =>
        other is not null && _entries.Count == other._entries.Count
        && _entries.All(pair => other._entries.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private readonly record struct Entry(long LastWriteTicks, long Length, FileAttributes Attributes);
}
