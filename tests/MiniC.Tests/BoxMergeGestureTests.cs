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
        Assert.Null(gesture.Update(Target, new Point(201, 200), 1999));
        Assert.Null(gesture.Update(null, new Point(700, 200), 2000));
        Assert.Null(gesture.ReadyTarget);
    }

    [Fact]
    public void StationaryHoverArmsButLeavingImmediatelyCancels()
    {
        var gesture = new BoxMergeGesture();
        gesture.Update(Target, new Point(200, 200), 1000);
        Assert.Equal(Target, gesture.Update(Target, new Point(200, 200), 2000));
        Assert.Null(gesture.Update(null, new Point(700, 200), 2001));
        Assert.Null(gesture.Update(Target, new Point(200, 200), 2500));
        Assert.Null(gesture.Update(Target, new Point(200, 200), 2999));
        Assert.Equal(Target, gesture.Update(Target, new Point(200, 200), 3500));
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
        Assert.Equal(Target, gesture.Update(Target, new Point(200, 200), 2000));
        Assert.Null(gesture.Update(Other, new Point(200, 200), 1500));
        Assert.Equal(Other, gesture.Update(Other, new Point(200, 200), 2500));
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
        Assert.Null(gesture.Update(Target, moved, 1999, dpi));
        Assert.Null(gesture.Update(Target, moved, 2000, dpi));
        Assert.Equal(Target, gesture.Update(Target, moved, 3000, dpi));
    }
}
