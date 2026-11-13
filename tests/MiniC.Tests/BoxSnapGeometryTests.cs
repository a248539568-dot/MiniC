using System;
using System.Windows;
using MiniC.Services;
using Xunit;

namespace MiniC.Tests;

public sealed class BoxSnapGeometryTests
{
    [Theory]
    [InlineData(106, 150, 100, 150)]
    [InlineData(174, 150, 180, 150)]
    [InlineData(305, 150, 300, 150)]
    [InlineData(-24, 150, -20, 150)]
    [InlineData(160, 105, 160, 100)]
    [InlineData(160, 184, 160, 190)]
    [InlineData(160, 284, 160, 280)]
    [InlineData(160, 14, 160, 10)]
    public void NearbyEdgesAlignWithoutResizing(double x, double y, double expectedX, double expectedY)
    {
        Assert.Equal(new Rect(expectedX, expectedY, 120, 90),
            BoxSnapGeometry.Snap(new Rect(x, y, 120, 90), [new Rect(100, 100, 200, 180)]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ThresholdIsSixDipsOnNegativeCoordinateAndScaledDisplays(double scale)
    {
        var target = new Rect(-500 * scale, -400 * scale, 200 * scale, 180 * scale);
        var within = new Rect(target.X + 6 * scale, target.Y + 5 * scale, target.Width, target.Height);
        var outside = new Rect(target.X + 7 * scale, target.Y + 7 * scale, target.Width, target.Height);
        Assert.Equal(target, BoxSnapGeometry.Snap(within, [target], scale));
        Assert.Equal(outside, BoxSnapGeometry.Snap(outside, [target], scale));
        Assert.Equal(within.Size, BoxSnapGeometry.Snap(within, [target], scale).Size);
    }

    [Fact]
    public void DistantBoxesDoNotAttractAndNearestEdgeWinsRegardlessOfOrder()
    {
        var moving = new Rect(104, 500, 200, 180);
        var first = new Rect(100, 500, 200, 180);
        var second = new Rect(108, 500, 200, 180);
        Assert.Equal(first, BoxSnapGeometry.Snap(moving, [second, first]));
        Assert.Equal(first, BoxSnapGeometry.Snap(moving, [first, second]));
        Assert.Equal(moving, BoxSnapGeometry.Snap(moving,
            [new Rect(100, 1000, 200, 180), new Rect(1000, 500, 200, 180)]));
        Assert.Equal(moving, BoxSnapGeometry.Snap(moving, [Rect.Empty, new Rect(0, 0, 0, 0)]));
        Assert.Equal(Rect.Empty, BoxSnapGeometry.Snap(Rect.Empty, [first]));
    }

    [Fact]
    public void CursorCanLeaveTheSmallSnapRangeWithoutAccumulatingOffsets()
    {
        var target = new Rect(100, 100, 200, 180);
        var near = new Rect(106, 150, 120, 90);
        var far = new Rect(107, 150, 120, 90);
        Assert.Equal(100, BoxSnapGeometry.Snap(near, [target]).X);
        Assert.Equal(far, BoxSnapGeometry.Snap(far, [target]));
        Assert.Equal(100, BoxSnapGeometry.Snap(near, [target]).X);
    }
}
