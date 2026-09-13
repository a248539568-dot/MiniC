using System.Windows;
using System.Windows.Controls;

namespace MiniC.Controls;

/// <summary>
/// 带位置缓动的自动换行面板，用于图标重排时产生平滑的“挤开”效果。
/// </summary>
public sealed class AnimatedWrapPanel : WrapPanel
{
    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        var previousPositions = PanelMotionAnimator.Capture(this);
        var result = base.ArrangeOverride(finalSize);
        PanelMotionAnimator.Animate(this, previousPositions);
        return result;
    }
}
