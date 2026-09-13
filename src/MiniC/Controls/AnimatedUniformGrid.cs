using System.Windows;
using System.Windows.Controls.Primitives;

namespace MiniC.Controls;

/// <summary>等分可用宽度并在标签换位时平滑移动的单行面板。</summary>
public sealed class AnimatedUniformGrid : UniformGrid
{
    public AnimatedUniformGrid() => Rows = 1;

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size arrangeSize)
    {
        var previousPositions = PanelMotionAnimator.Capture(this);
        var result = base.ArrangeOverride(arrangeSize);
        PanelMotionAnimator.Animate(this, previousPositions);
        return result;
    }
}
