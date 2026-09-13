using MiniC.Models;
using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>为自绘桌面图标提供统一的网格吸附、边界限制和空位避让。</summary>
public sealed class DesktopIconPlacementService
{
    public const int TileWidth = 82;
    public const int TileHeight = 88;
    private const int GridOriginOffset = 16;
    private readonly Func<IReadOnlyList<System.Drawing.Rectangle>> _readWorkingAreas;
    private readonly Func<double> _readScale;
    private int CellWidth => (int)Math.Ceiling(TileWidth * _readScale());
    private int CellHeight => (int)Math.Ceiling(TileHeight * _readScale());
    private int OriginOffset => (int)Math.Ceiling(GridOriginOffset * _readScale());

    public DesktopIconPlacementService()
        : this(() => System.Windows.Forms.Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray(),
            () => GetDpiForSystem() / 96.0)
    {
    }

    internal DesktopIconPlacementService(Func<IReadOnlyList<System.Drawing.Rectangle>> readWorkingAreas,
        double scale = 1)
        : this(readWorkingAreas, () => scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
    }

    private DesktopIconPlacementService(Func<IReadOnlyList<System.Drawing.Rectangle>> readWorkingAreas,
        Func<double> readScale)
    {
        _readWorkingAreas = readWorkingAreas;
        _readScale = readScale;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public bool PlaceMissing(
        IReadOnlyList<DesktopItem> items,
        IDictionary<string, DesktopIconPositionState> positions,
        IReadOnlyDictionary<string, System.Windows.Point> preferredPositions,
        bool usePreferredPositions = true)
    {
        var occupied = RepairExistingOverlaps(items, positions, out var changed);

        foreach (var item in items
                     .Where(item => !positions.ContainsKey(item.Path))
                     .OrderBy(item => item.LastWriteTicks)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            var cell = usePreferredPositions && preferredPositions.TryGetValue(item.Path, out var preferred)
                ? FindNearestAvailable((int)Math.Round(preferred.X), (int)Math.Round(preferred.Y), occupied)
                : FindFirstAvailable(occupied);
            occupied.Add(cell);
            positions[item.Path] = new DesktopIconPositionState { X = cell.X, Y = cell.Y };
            changed = true;
        }
        return changed;
    }

    public void PlaceAt(
        IReadOnlyList<string> paths,
        System.Windows.Point screenPoint,
        IReadOnlyList<DesktopItem> visibleItems,
        IDictionary<string, DesktopIconPositionState> positions)
    {
        var moving = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occupied = visibleItems
            .Where(item => !moving.Contains(item.Path) && positions.TryGetValue(item.Path, out _))
            .Select(item =>
            {
                var position = positions[item.Path];
                return new GridCell(position.X, position.Y);
            })
            .ToList();

        var requestedX = (int)Math.Round(screenPoint.X) - CellWidth / 2;
        var requestedY = (int)Math.Round(screenPoint.Y) - CellHeight / 2;
        DesktopIconPositionState? anchorPosition = null;
        var hasAnchor = paths.Count > 0 && positions.TryGetValue(paths[0], out anchorPosition);
        var deltaX = hasAnchor ? requestedX - anchorPosition!.X : 0;
        var deltaY = hasAnchor ? requestedY - anchorPosition!.Y : 0;
        for (var index = 0; index < paths.Count; index++)
        {
            var hasOriginal = positions.TryGetValue(paths[index], out var original);
            var targetX = hasAnchor && hasOriginal
                ? original!.X + deltaX
                : requestedX + index % 4 * CellWidth;
            var targetY = hasAnchor && hasOriginal
                ? original!.Y + deltaY
                : requestedY + index / 4 * CellHeight;
            var cell = FindNearestAvailable(
                targetX,
                targetY,
                occupied);
            occupied.Add(cell);
            positions[paths[index]] = new DesktopIconPositionState { X = cell.X, Y = cell.Y };
        }
    }

    /// <summary>仿照 Windows 桌面自动排列，按当前视觉顺序压紧到连续网格。</summary>
    public bool AutoArrange(
        IReadOnlyList<DesktopItem> items,
        IDictionary<string, DesktopIconPositionState> positions)
    {
        var orderedItems = items
            .Where(item => positions.ContainsKey(item.Path))
            .OrderBy(item => Snap(positions[item.Path].X, positions[item.Path].Y).X)
            .ThenBy(item => Snap(positions[item.Path].X, positions[item.Path].Y).Y)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cells = EnumerateCellsColumnFirst().Take(orderedItems.Length).ToArray();
        var changed = false;
        for (var index = 0; index < cells.Length; index++)
        {
            var position = positions[orderedItems[index].Path];
            if (position.X == cells[index].X && position.Y == cells[index].Y) continue;
            position.X = cells[index].X;
            position.Y = cells[index].Y;
            changed = true;
        }
        return changed;
    }

    private List<GridCell> RepairExistingOverlaps(
        IReadOnlyList<DesktopItem> items,
        IDictionary<string, DesktopIconPositionState> positions,
        out bool changed)
    {
        changed = false;
        var occupied = new List<GridCell>();
        foreach (var item in items
                     .Where(item => positions.ContainsKey(item.Path))
                     .OrderBy(item => positions[item.Path].X)
                     .ThenBy(item => positions[item.Path].Y)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            var position = positions[item.Path];
            var requested = new GridCell(position.X, position.Y);
            var cell = IsInWorkingArea(requested) && IsAvailable(requested, occupied)
                ? requested
                : FindNearestAvailable(position.X, position.Y, occupied);
            if (cell != requested)
            {
                changed = true;
                position.X = cell.X;
                position.Y = cell.Y;
            }
            occupied.Add(cell);
        }
        return occupied;
    }

    private GridCell FindFirstAvailable(IReadOnlyCollection<GridCell> occupied) =>
        EnumerateCellsColumnFirst().FirstOrDefault(cell => IsAvailable(cell, occupied), Snap(0, 0));

    private GridCell FindNearestAvailable(
        int requestedX,
        int requestedY,
        IReadOnlyCollection<GridCell> occupied)
    {
        var requested = Snap(requestedX, requestedY);
        if (IsAvailable(requested, occupied)) return requested;

        return EnumerateCells()
            .Where(cell => IsAvailable(cell, occupied))
            .OrderBy(cell => DistanceSquared(cell, requested))
            .ThenBy(cell => cell.Y)
            .ThenBy(cell => cell.X)
            .FirstOrDefault(requested);
    }

    private bool IsAvailable(GridCell candidate, IEnumerable<GridCell> occupied) =>
        occupied.All(existing => Math.Abs((long)candidate.X - existing.X) >= CellWidth
                                 || Math.Abs((long)candidate.Y - existing.Y) >= CellHeight);

    private GridCell Snap(int x, int y)
    {
        var requested = new GridCell(x, y);
        return GetBounds().Select(bounds =>
        {
            var column = Math.Clamp((int)Math.Round((x - bounds.Left) / (double)CellWidth),
                0, (bounds.Width - CellWidth) / CellWidth);
            var row = Math.Clamp((int)Math.Round((y - bounds.Top) / (double)CellHeight),
                0, (bounds.Height - CellHeight) / CellHeight);
            return new GridCell(bounds.Left + column * CellWidth, bounds.Top + row * CellHeight);
        }).MinBy(cell => DistanceSquared(cell, requested));
    }

    private IEnumerable<GridCell> EnumerateCells()
    {
        foreach (var bounds in GetBounds())
        for (var y = bounds.Top; y <= bounds.Bottom - CellHeight; y += CellHeight)
        for (var x = bounds.Left; x <= bounds.Right - CellWidth; x += CellWidth)
            yield return new GridCell(x, y);
    }

    private IEnumerable<GridCell> EnumerateCellsColumnFirst()
    {
        foreach (var bounds in GetBounds())
        for (var x = bounds.Left; x <= bounds.Right - CellWidth; x += CellWidth)
        for (var y = bounds.Top; y <= bounds.Bottom - CellHeight; y += CellHeight)
            yield return new GridCell(x, y);
    }

    private bool IsInWorkingArea(GridCell cell) => GetBounds().Any(bounds =>
        cell.X >= bounds.Left && cell.Y >= bounds.Top
        && (long)cell.X + CellWidth <= bounds.Right && (long)cell.Y + CellHeight <= bounds.Bottom);

    private IEnumerable<System.Drawing.Rectangle> GetBounds() => _readWorkingAreas()
        .Where(area => area.Width >= CellWidth + OriginOffset && area.Height >= CellHeight + OriginOffset)
        .OrderBy(area => area.Left).ThenBy(area => area.Top)
        .Select(area => new System.Drawing.Rectangle(area.Left + OriginOffset, area.Top + OriginOffset,
            area.Width - OriginOffset, area.Height - OriginOffset));

    private static long DistanceSquared(GridCell left, GridCell right)
    {
        var deltaX = (long)left.X - right.X;
        var deltaY = (long)left.Y - right.Y;
        return deltaX * deltaX + deltaY * deltaY;
    }

    private readonly record struct GridCell(int X, int Y);
}
