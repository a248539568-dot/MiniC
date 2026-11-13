using MiniC.Models;
using MiniC.Services;
using Xunit;
using Point = System.Windows.Point;

namespace MiniC.Tests;

public sealed class BoxMergeGestureTests
{
    private static readonly BoxTabRef Target = new(BoxContentKind.DesktopGroup, "target");
    private static readonly BoxTabRef Other = new(BoxContentKind.Widget, "other");

    [Fact]
    public void PassingThroughTargetCannotArmMerge()
    {
        var gesture = new BoxMergeGesture();
        Assert.Null(gesture.Update(Target, new Point(200, 200), 1000));
        Assert.Null(gesture.Update(Target, new Point(201, 200), 1199));
        Assert.Null(gesture.Update(null, new Point(700, 200), 1200));
        Assert.Null(gesture.ReadyTarget);
    }

    [Fact]
    public void StationaryHoverArmsButLeavingImmediatelyCancels()
    {
        var gesture = new BoxMergeGesture();
        gesture.Update(Target, new Point(200, 200), 1000);
        Assert.Equal(Target, gesture.Update(Target, new Point(200, 200), 1200));
        Assert.Null(gesture.Update(null, new Point(700, 200), 1201));
        Assert.Null(gesture.Update(Target, new Point(200, 200), 1500));
        Assert.Null(gesture.Update(Target, new Point(200, 200), 1699));
        Assert.Equal(Target, gesture.Update(Target, new Point(200, 200), 1700));
    }

    [Fact]
    public void InitialOverlapRequiresLeavingAndEnteringAgain()
    {
        var gesture = new BoxMergeGesture();
        gesture.Reset(Target);
        Assert.Null(gesture.Update(Target, new Point(200, 200), 1000));
        Assert.Null(gesture.Update(Target, new Point(200, 200), 10000));
        Assert.Null(gesture.Update(null, new Point(700, 200), 10001));
        Assert.Null(gesture.Update(Target, new Point(200, 200), 10002));
        Assert.Equal(Target, gesture.Update(Target, new Point(200, 200), 11002));
    }

    [Fact]
    public void ChangingTargetsRestartsDwellAndCancellationClearsIt()
    {
        var gesture = new BoxMergeGesture();
        gesture.Update(Target, new Point(200, 200), 1000);
        Assert.Equal(Target, gesture.Update(Target, new Point(200, 200), 1200));
        Assert.Null(gesture.Update(Other, new Point(200, 200), 1500));
        Assert.Equal(Other, gesture.Update(Other, new Point(200, 200), 1700));
        gesture.Reset();
        Assert.Null(gesture.ReadyTarget);
        Assert.Null(gesture.Update(Other, new Point(200, 200), 2000));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void MovingInsideTheTargetRestartsDwellInDpiScaledDistance(double dpi)
    {
        var gesture = new BoxMergeGesture();
        gesture.Update(Target, new Point(200, 200), 1000, dpi);
        var moved = new Point(200 + 11 * dpi, 200);
        Assert.Null(gesture.Update(Target, moved, 1199, dpi));
        Assert.Null(gesture.Update(Target, moved, 1200, dpi));
        Assert.Equal(Target, gesture.Update(Target, moved, 1400, dpi));
    }
}
