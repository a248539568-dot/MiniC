namespace MiniC.Services;

/// <summary>
/// 在更新程序短暂删除并重建桌面项目时保留视觉归属，持续缺失后才清理失效归属。
/// </summary>
public sealed class DesktopAssignmentRetentionService
{
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromMinutes(5);

    private readonly long _gracePeriodMilliseconds;
    private readonly Dictionary<string, long> _missingSince = new(StringComparer.OrdinalIgnoreCase);

    public DesktopAssignmentRetentionService(TimeSpan? gracePeriod = null)
    {
        var effectiveGracePeriod = gracePeriod ?? DefaultGracePeriod;
        if (effectiveGracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        _gracePeriodMilliseconds = checked((long)effectiveGracePeriod.TotalMilliseconds);
    }

    public IReadOnlyList<string> Reconcile(
        IDictionary<string, string> assignments,
        IReadOnlySet<string> existingPaths,
        long nowMilliseconds)
    {
        var removedPaths = new List<string>();
        foreach (var path in assignments.Keys.ToArray())
        {
            if (existingPaths.Contains(path))
            {
                _missingSince.Remove(path);
                continue;
            }

            if (!_missingSince.TryGetValue(path, out var missingSince))
            {
                _missingSince[path] = nowMilliseconds;
                continue;
            }

            if (nowMilliseconds - missingSince < _gracePeriodMilliseconds) continue;
            assignments.Remove(path);
            _missingSince.Remove(path);
            removedPaths.Add(path);
        }

        foreach (var path in _missingSince.Keys.Where(path => !assignments.ContainsKey(path)).ToArray())
            _missingSince.Remove(path);
        return removedPaths;
    }

}
