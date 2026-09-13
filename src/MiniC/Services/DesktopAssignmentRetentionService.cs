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

    internal static bool ValidateForSmokeTest()
    {
        var service = new DesktopAssignmentRetentionService(TimeSpan.FromSeconds(3));
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:\\Desktop\\Updater.lnk"] = "box-one"
        };
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(["C:\\Desktop\\Updater.lnk"], StringComparer.OrdinalIgnoreCase);

        var firstMissing = service.Reconcile(assignments, missing, 1_000);
        var restored = service.Reconcile(assignments, present, 2_000);
        var missingAgain = service.Reconcile(assignments, missing, 3_000);
        var stillRetained = service.Reconcile(assignments, missing, 5_999);
        var expired = service.Reconcile(assignments, missing, 6_000);
        return firstMissing.Count == 0
               && restored.Count == 0
               && missingAgain.Count == 0
               && stillRetained.Count == 0
               && assignments.Count == 0
               && expired.SequenceEqual(["C:\\Desktop\\Updater.lnk"], StringComparer.OrdinalIgnoreCase);
    }
}
