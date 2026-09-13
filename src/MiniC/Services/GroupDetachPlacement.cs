using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MiniC.Services;

/// <summary>预览和实际拆出共享同一 DIP 坐标落点，避免松手后窗口跳位。</summary>
internal static class GroupDetachPlacement
{
    public static Rect GetBounds(Point pointer, Size size, Rect desktop) => new(
        Math.Clamp(pointer.X - size.Width / 2, desktop.Left, Math.Max(desktop.Left, desktop.Right - size.Width)),
        Math.Clamp(pointer.Y - 18, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - 42)),
        size.Width, size.Height);
}
