using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace MiniC.Controls;

/// <summary>为面板子项的位置变化添加统一的弹性回位动画。</summary>
internal static class PanelMotionAnimator
{
    private static readonly Duration MoveDuration = new(TimeSpan.FromMilliseconds(170));
    private static readonly CubicEase MoveEasing = CreateEasing();

    internal readonly record struct ItemPosition(Point Visible, Vector Layout);

    public static Dictionary<UIElement, ItemPosition> Capture(Panel panel)
    {
        var positions = new Dictionary<UIElement, ItemPosition>();
        foreach (UIElement child in panel.Children)
        {
            // 集合换位会先标记 Arrange 无效，但旧视觉位置仍有效，必须保留以连续衔接。
            if (child.RenderSize.Width > 0)
                positions[child] = new ItemPosition(child.TranslatePoint(new Point(), panel), VisualTreeHelper.GetOffset(child));
        }
        return positions;
    }

    public static void Animate(Panel panel, IReadOnlyDictionary<UIElement, ItemPosition> previousPositions)
    {
        foreach (UIElement child in panel.Children)
        {
            if (!previousPositions.TryGetValue(child, out var previous)) continue;
            var current = VisualTreeHelper.GetOffset(child);
            // 同一布局的重排请求不能中断仍在运行的换位动画。
            if (current == previous.Layout) continue;
            var deltaX = previous.Visible.X - current.X;
            var deltaY = previous.Visible.Y - current.Y;
            if (!SystemParameters.ClientAreaAnimation)
            {
                child.ClearValue(UIElement.RenderTransformProperty);
                continue;
            }
            if (Math.Abs(deltaX) < 0.5 && Math.Abs(deltaY) < 0.5)
            {
                child.ClearValue(UIElement.RenderTransformProperty);
                continue;
            }

            // 时钟首帧尚未采样时也保持原可见位置，防止先跳到目标再回弹。
            var transform = new TranslateTransform(deltaX, deltaY);
            child.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, MoveDuration) { EasingFunction = MoveEasing });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, MoveDuration) { EasingFunction = MoveEasing });
        }
    }

    private static CubicEase CreateEasing()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }
}
