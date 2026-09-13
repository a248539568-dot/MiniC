using System.Collections.ObjectModel;

namespace MiniC.Services;

/// <summary>按当前相对顺序把一组项目移动到集合中的插入位置。</summary>
public static class CollectionReorderService
{
    /// <summary>把项目块移动到拖动指针对应的最终可视位置。</summary>
    public static bool MoveBlockToIndex<T>(
        ObservableCollection<T> source,
        IReadOnlyCollection<T> movingItems,
        int targetIndex)
        where T : class
    {
        if (source.Count < 2 || movingItems.Count == 0) return false;
        var movingSet = movingItems.ToHashSet(ReferenceEqualityComparer.Instance);
        var block = source.Where(movingSet.Contains).ToArray();
        if (block.Length == 0 || block.Length == source.Count) return false;

        var remaining = source.Where(item => !movingSet.Contains(item)).ToList();
        targetIndex = Math.Clamp(targetIndex, 0, remaining.Count);
        remaining.InsertRange(targetIndex, block);
        if (source.SequenceEqual(remaining, ReferenceEqualityComparer.Instance)) return false;

        ApplyOrder(source, remaining);
        return true;
    }

    public static bool MoveBlock<T>(
        ObservableCollection<T> source,
        IReadOnlyCollection<T> movingItems,
        int insertionIndex)
        where T : class
    {
        if (source.Count < 2 || movingItems.Count == 0) return false;
        var movingSet = movingItems.ToHashSet(ReferenceEqualityComparer.Instance);
        var block = source.Where(movingSet.Contains).ToArray();
        if (block.Length == 0 || block.Length == source.Count) return false;

        insertionIndex = Math.Clamp(insertionIndex, 0, source.Count);
        var removedBeforeInsertion = source.Take(insertionIndex).Count(movingSet.Contains);
        var remaining = source.Where(item => !movingSet.Contains(item)).ToList();
        var adjustedIndex = Math.Clamp(insertionIndex - removedBeforeInsertion, 0, remaining.Count);
        remaining.InsertRange(adjustedIndex, block);
        if (source.SequenceEqual(remaining, ReferenceEqualityComparer.Instance)) return false;

        ApplyOrder(source, remaining);
        return true;
    }

    private static void ApplyOrder<T>(ObservableCollection<T> source, IReadOnlyList<T> order)
    {
        for (var index = 0; index < order.Count; index++)
        {
            var currentIndex = source.IndexOf(order[index]);
            if (currentIndex != index) source.Move(currentIndex, index);
        }
    }
}
