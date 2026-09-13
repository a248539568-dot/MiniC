using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MiniC.Controls;

/// <summary>绘制等尺寸浮动卡片及两片表面之间的柔性连接；只处理视觉，不修改布局模型。</summary>
internal sealed class GroupTransitionSurface : FrameworkElement
{
    private static readonly Brush ShadowBrush = CreateShadowBrush();

    private static Brush CreateShadowBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(7, 25, 31, 40));
        brush.Freeze();
        return brush;
    }
    public static readonly DependencyProperty CardPositionProperty = DependencyProperty.Register(
        nameof(CardPosition), typeof(Point), typeof(GroupTransitionSurface),
        new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DockProgressProperty = DependencyProperty.Register(
        nameof(DockProgress), typeof(double), typeof(GroupTransitionSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public Point CardPosition
    {
        get => (Point)GetValue(CardPositionProperty);
        set => SetValue(CardPositionProperty, value);
    }

    public double DockProgress
    {
        get => (double)GetValue(DockProgressProperty);
        set => SetValue(DockProgressProperty, value);
    }

    public required ImageSource Snapshot { get; init; }
    public required Size CardSize { get; init; }
    public required Brush ConnectionBrush { get; init; }
    public Point WorldOrigin { get; set; }
    public Rect AnchorBounds { get; set; }
    public bool IsDocking { get; set; }
    public Vector DockDirection { get; set; }
    internal Rect CardBounds => new(CardPosition, CardSize);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.PushTransform(new TranslateTransform(-WorldOrigin.X, -WorldOrigin.Y));
        var card = CardBounds;
        var connection = CreateConnection(AnchorBounds, card);
        if (connection is not null)
        {
            dc.PushOpacity(0.72 * (1 - DockProgress));
            dc.DrawGeometry(ConnectionBrush, null, connection);
            dc.Pop();
        }

        // 卡片保持原尺寸，沿目标边缘被遮罩吸收，文字和图标不被缩放。
        if (IsDocking && DockProgress > 0)
        {
            var reveal = card;
            if (Math.Abs(DockDirection.X) >= Math.Abs(DockDirection.Y))
            {
                reveal.Width *= 1 - DockProgress;
                if (DockDirection.X < 0) reveal.X = card.Right - reveal.Width;
            }
            else
            {
                reveal.Height *= 1 - DockProgress;
                if (DockDirection.Y < 0) reveal.Y = card.Bottom - reveal.Height;
            }
            dc.PushClip(new RectangleGeometry(reveal));
        }
        for (var index = 3; index >= 1; index--)
        {
            var shadow = card;
            shadow.Inflate(index * 3, index * 3);
            shadow.Offset(0, 4);
            dc.DrawRoundedRectangle(ShadowBrush, null,
                shadow, 18 + index * 3, 18 + index * 3);
        }
        dc.PushClip(new RectangleGeometry(card, 18, 18));
        dc.DrawImage(Snapshot, card);
        dc.Pop();
        if (IsDocking && DockProgress > 0) dc.Pop();
        dc.Pop();
    }

    internal static StreamGeometry? CreateConnection(Rect anchor, Rect card)
    {
        if (anchor.IsEmpty || card.IsEmpty || anchor.Width < 48 || anchor.Height < 48
            || card.Width < 48 || card.Height < 48) return null;
        var delta = new Point(card.Left + card.Width / 2, card.Top + card.Height / 2)
                    - new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
        var horizontal = Math.Abs(delta.X) / Math.Max(1, anchor.Width)
                         >= Math.Abs(delta.Y) / Math.Max(1, anchor.Height);
        var positive = horizontal ? delta.X >= 0 : delta.Y >= 0;
        Point a;
        Point b;
        if (horizontal)
        {
            var y = Math.Clamp(card.Top + card.Height * 0.38, anchor.Top + 24, anchor.Bottom - 24);
            a = new Point(positive ? anchor.Right - 5 : anchor.Left + 5, y);
            b = new Point(positive ? card.Left + 5 : card.Right - 5,
                Math.Clamp(y, card.Top + 24, card.Bottom - 24));
        }
        else
        {
            var x = Math.Clamp(card.Left + card.Width / 2, anchor.Left + 24, anchor.Right - 24);
            a = new Point(x, positive ? anchor.Bottom - 5 : anchor.Top + 5);
            b = new Point(Math.Clamp(x, card.Left + 24, card.Right - 24),
                positive ? card.Top + 5 : card.Bottom - 5);
        }
        var gap = horizontal ? (b.X - a.X) * (positive ? 1 : -1)
                             : (b.Y - a.Y) * (positive ? 1 : -1);
        // 超过拉伸距离后连接自然变细消失，不在相距很远的窗口之间拉出长带。
        if (gap < -24 || gap > 112) return null;
        var width = 24 * Math.Pow(1 - Math.Max(0, gap) / 112, 0.65);
        var normal = horizontal ? new Vector(0, width) : new Vector(width, 0);
        var neck = normal * 0.14;
        var mid = a + (b - a) * 0.5;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(a - normal, true, true);
            context.BezierTo(mid - neck, mid - neck, b - normal, true, false);
            context.LineTo(b + normal, true, false);
            context.BezierTo(mid + neck, mid + neck, a + normal, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
