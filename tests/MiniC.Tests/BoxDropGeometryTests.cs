using System.Windows;
using System.Windows.Media;
using System.Threading.Tasks;
using MiniC.Services;
using MiniC.Models;
using Xunit;
using Point = System.Windows.Point;

namespace MiniC.Tests;

public sealed class BoxDropGeometryTests
{
    [Fact]
    public void DropMustLandInVisibleCenterOfTargetBox()
    {
        var bounds = new Rect(100, 200, 400, 300);
        Assert.Equal(new Rect(180, 260, 240, 180), BoxDropGeometry.Center(bounds));
        Assert.True(BoxDropGeometry.Contains(bounds, new Point(300, 350)));
        Assert.False(BoxDropGeometry.Contains(bounds, new Point(110, 210)));
        Assert.True(BoxDropGeometry.Contains(bounds, new Point(180, 260)));
        Assert.False(BoxDropGeometry.Contains(bounds, new Point(99, 210)));
        Assert.False(BoxDropGeometry.Contains(Rect.Empty, new Point()));
    }

    [Fact]
    public void FrontBoxEdgeDoesNotMergeIntoTheCenterOfABoxBehindIt()
    {
        var front = new BoxDropTarget("front", new(BoxContentKind.DesktopGroup, "front"), new Rect(150, 100, 400, 300));
        var back = new BoxDropTarget("back", new(BoxContentKind.DesktopGroup, "back"), new Rect(100, 100, 400, 300));
        Assert.Null(BoxDropGeometry.FindTarget("source", [front, back], new Point(190, 250)));
        Assert.Equal(back.Tab, BoxDropGeometry.FindTarget("source", [back, front], new Point(190, 250)));
    }

    [Fact]
    public void SourceIsSkippedRegardlessOfItsWindowOrder()
    {
        var source = new BoxDropTarget("source", new(BoxContentKind.DesktopGroup, "source"), new Rect(100, 100, 400, 300));
        var target = new BoxDropTarget("target", new(BoxContentKind.Widget, "target"), source.Bounds);
        Assert.Equal(target.Tab, BoxDropGeometry.FindTarget("source", [source, target], new Point(300, 250)));
        Assert.Equal(target.Tab, BoxDropGeometry.FindTarget("source", [target, source], new Point(300, 250)));
        Assert.Null(BoxDropGeometry.FindTarget("source", [source], new Point(300, 250)));
    }

    [Fact]
    public void WholeBoxDragAcceptsTheTargetBoundsWhileTabDragKeepsTheCenterZone()
    {
        var target = new BoxDropTarget("target", new(BoxContentKind.DesktopGroup, "target"), new Rect(100, 100, 400, 300));
        var edge = new Point(110, 250);
        Assert.Null(BoxDropGeometry.FindTarget("source", [target], edge));
        Assert.Equal(target.Tab, BoxDropGeometry.FindTarget("source", [target], edge, wholeBox: true));
    }

    [Fact]
    public async Task RenderScaleDoesNotChangeLayoutBoundsUsedForDrop()
    {
        await GroupMotionTests.OnUiThread(() =>
        {
            var shell = new System.Windows.Controls.Border
            {
                Width = 320, Height = 220,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.38, 0.38)
            };
            shell.Measure(new Size(320, 220));
            shell.Arrange(new Rect(0, 0, 320, 220));

            var origin = VisualTreeHelper.GetOffset(shell);
            var layoutBounds = new Rect(origin.X, origin.Y, shell.ActualWidth, shell.ActualHeight);
            Assert.Equal(new Rect(0, 0, 320, 220), layoutBounds);
            Assert.True(BoxDropGeometry.Contains(layoutBounds, new Point(160, 110)));
            Assert.Equal(0.38, ((ScaleTransform)shell.RenderTransform).ScaleX);
            return Task.CompletedTask;
        });
    }
}
