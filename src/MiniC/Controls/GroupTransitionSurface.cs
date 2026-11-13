using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MiniC.Controls;

/// <summary>绘制标签拆出的缩放预览及柔性连接；不修改实际窗口布局。</summary>
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
    public static readonly DependencyProperty PreviewScaleProperty = DependencyProperty.Register(
        nameof(PreviewScale), typeof(double), typeof(GroupTransitionSurface),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double PreviewScale
    {
        get => (double)GetValue(PreviewScaleProperty);
        set => SetValue(PreviewScaleProperty, value);
    }

    public Point CardPosition
    {
        get => (Point)GetValue(CardPositionProperty);
        set => SetValue(CardPositionProperty, value);
    }

    public required ImageSource Snapshot { get; init; }
    public required Size CardSize { get; init; }
    public required Brush ConnectionBrush { get; init; }
    public double Radius { get; set; } = GroupGeometry.Radius;
    public Point WorldOrigin { get; set; }
    public Rect AnchorBounds { get; set; }
    public Vector ScaleAnchor { get; set; }
    internal Rect CardBounds => new(CardPosition + ScaleAnchor * (1 - PreviewScale),
        new Size(CardSize.Width * PreviewScale, CardSize.Height * PreviewScale));
    internal double CardRadius => Radius * PreviewScale;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.PushTransform(new TranslateTransform(-WorldOrigin.X, -WorldOrigin.Y));
        var card = CardBounds;
        var connection = CreateConnection(AnchorBounds, card);
        if (connection is not null)
        {
            dc.PushOpacity(0.72);
            dc.DrawGeometry(ConnectionBrush, null, connection);
            dc.Pop();
        }

        for (var index = 3; index >= 1; index--)
        {
            var shadow = card;
            shadow.Inflate(index * 3, index * 3);
            shadow.Offset(0, 4);
            dc.DrawRoundedRectangle(ShadowBrush, null,
                shadow, CardRadius + index * 3, CardRadius + index * 3);
        }
        dc.PushClip(new RectangleGeometry(card, CardRadius, CardRadius));
        dc.DrawImage(Snapshot, card);
        dc.Pop();
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
