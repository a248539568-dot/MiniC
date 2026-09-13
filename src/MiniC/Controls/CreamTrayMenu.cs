using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MiniC.Controls;

/// <summary>
/// 奶白色圆角托盘菜单，统一处理窗口激活、圆角区域和菜单关闭行为。
/// </summary>
internal sealed class CreamTrayMenu : ContextMenuStrip
{
    private const int MenuWidth = 204;

    public CreamTrayMenu()
    {
        BackColor = Color.FromArgb(255, 251, 242);
        ForeColor = Color.FromArgb(49, 45, 40);
        Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        Padding = new Padding(8, 10, 8, 10);
        MinimumSize = new Size(MenuWidth, 0);
        AutoClose = true;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        Renderer = new CreamMenuRenderer();
    }

    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        if (e.Item is not { } item) return;
        if (item is ToolStripSeparator)
        {
            item.AutoSize = false;
            item.Size = new Size(MenuWidth - Padding.Horizontal, 13);
            item.Margin = Padding.Empty;
        }
        else
        {
            item.AutoSize = false;
            item.Size = new Size(MenuWidth - Padding.Horizontal, 38);
            item.Padding = Padding.Empty;
            item.Margin = Padding.Empty;
            item.TextAlign = ContentAlignment.MiddleLeft;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateRoundedRegion();
        SetForegroundWindow(Handle);
    }

    protected override void OnItemClicked(ToolStripItemClickedEventArgs e)
    {
        base.OnItemClicked(e);
        Close(ToolStripDropDownCloseReason.ItemClicked);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0 || IsDisposed || !IsHandleCreated) return;
        var regionHandle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18);
        try
        {
            Region?.Dispose();
            Region = Region.FromHrgn(regionHandle);
        }
        finally
        {
            DeleteObject(regionHandle);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}

internal sealed class CreamMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color BackgroundColor = Color.FromArgb(255, 251, 242);
    private static readonly Color HoverColor = Color.FromArgb(241, 232, 216);
    private static readonly Color BorderColor = Color.FromArgb(222, 210, 190);
    private static readonly Color TextColor = Color.FromArgb(49, 45, 40);
    private static readonly Color AccentColor = Color.FromArgb(92, 112, 102);

    public CreamMenuRenderer() : base(new CreamMenuColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(BackgroundColor);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(3, 2, e.Item.Width - 6, e.Item.Height - 4);
        using var path = RoundedRectangle(bounds, 8);
        using var brush = new SolidBrush(HoverColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is ToolStripSeparator) return;

        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var textBounds = new Rectangle(38, 0, Math.Max(0, e.Item.Width - 50), e.Item.Height);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, textBounds, TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (e.Item is ToolStripMenuItem { Checked: true }) DrawCheckedState(e.Graphics, e.Item.Height);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // 勾选状态使用矢量路径绘制，避免依赖字体字符导致不同系统下错位。
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(224, 214, 198));
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    private static void DrawCheckedState(Graphics graphics, int itemHeight)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(12, (itemHeight - 16) / 2, 16, 16);
        using var background = new SolidBrush(AccentColor);
        using var boxPath = RoundedRectangle(box, 5);
        graphics.FillPath(background, boxPath);

        using var checkPen = new Pen(Color.White, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(checkPen,
        [
            new Point(box.Left + 4, box.Top + 8),
            new Point(box.Left + 7, box.Top + 11),
            new Point(box.Left + 12, box.Top + 5)
        ]);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = RoundedRectangle(bounds, 9);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class CreamMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(255, 251, 242);
    public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
    public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
    public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    public override Color MenuItemSelected => Color.FromArgb(241, 232, 216);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color SeparatorDark => Color.FromArgb(224, 214, 198);
    public override Color SeparatorLight => Color.FromArgb(224, 214, 198);
}
