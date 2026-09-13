using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiniC.ViewModels;
using Point = System.Windows.Point;

namespace MiniC.Controls;

/// <summary>在指定表面内绘制选择框，并按图标可视边界更新选择状态。</summary>
public sealed class MarqueeSelectionController
{
    private readonly FrameworkElement _surface;
    private readonly FrameworkElement _marquee;
    private readonly Func<IEnumerable<DesktopItem>> _items;
    private readonly Func<DesktopItem, FrameworkElement?> _resolveTile;
    private Dictionary<DesktopItem, bool> _initialSelection = [];
    private Point _origin;
    private bool _toggleSelection;

    public MarqueeSelectionController(
        FrameworkElement surface,
        FrameworkElement marquee,
        Func<IEnumerable<DesktopItem>> items,
        Func<DesktopItem, FrameworkElement?> resolveTile)
    {
        _surface = surface;
        _marquee = marquee;
        _items = items;
        _resolveTile = resolveTile;
    }

    public bool IsActive { get; private set; }

    public void Begin(Point origin, bool toggleSelection)
    {
        Cancel();
        _origin = Clamp(origin);
        _toggleSelection = toggleSelection;
        _initialSelection = _items().ToDictionary(item => item, item => item.IsSelected);
        if (!toggleSelection)
        {
            foreach (var item in _initialSelection.Keys) item.IsSelected = false;
        }

        _marquee.Visibility = Visibility.Collapsed;
        IsActive = true;
        _surface.CaptureMouse();
    }

    public void Update(Point current)
    {
        if (!IsActive) return;
        current = Clamp(current);
        var width = Math.Abs(current.X - _origin.X);
        var height = Math.Abs(current.Y - _origin.Y);
        if (_marquee.Visibility != Visibility.Visible
            && width < SystemParameters.MinimumHorizontalDragDistance
            && height < SystemParameters.MinimumVerticalDragDistance)
            return;

        var bounds = new Rect(
            Math.Min(_origin.X, current.X),
            Math.Min(_origin.Y, current.Y),
            width,
            height);
        Canvas.SetLeft(_marquee, bounds.Left);
        Canvas.SetTop(_marquee, bounds.Top);
        _marquee.Width = Math.Max(1, bounds.Width);
        _marquee.Height = Math.Max(1, bounds.Height);
        _marquee.Visibility = Visibility.Visible;

        foreach (var (item, initiallySelected) in _initialSelection)
        {
            var tile = _resolveTile(item);
            var intersects = tile is not null && Intersects(tile, bounds);
            item.IsSelected = _toggleSelection ? initiallySelected != intersects : intersects;
        }
    }

    public void Complete(Point current)
    {
        if (!IsActive) return;
        Update(current);
        End();
    }

    public void Cancel()
    {
        if (!IsActive)
        {
            _marquee.Visibility = Visibility.Collapsed;
            return;
        }
        End();
    }

    private bool Intersects(FrameworkElement tile, Rect selectionBounds)
    {
        try
        {
            var tileBounds = tile.TransformToAncestor(_surface)
                .TransformBounds(new Rect(new Point(), tile.RenderSize));
            return selectionBounds.IntersectsWith(tileBounds);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, _surface.ActualWidth)),
        Math.Clamp(point.Y, 0, Math.Max(0, _surface.ActualHeight)));

    private void End()
    {
        IsActive = false;
        _marquee.Visibility = Visibility.Collapsed;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        _initialSelection.Clear();
    }
}
