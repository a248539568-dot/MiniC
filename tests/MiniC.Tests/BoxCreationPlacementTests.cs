using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MiniC.Services;
using Xunit;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MiniC.Tests;

public sealed class BoxCreationPlacementTests
{
    [Fact]
    public void RepeatedCreationUsesAvailableSpaceInsteadOfStackingBoxes()
    {
        var desktop = new Rect(0, 0, 1920, 1040);
        var size = new Size(340, 380);
        var occupied = new List<Rect>();
        var preferred = new Point(70, 130);
        for (var i = 0; i < 10; i++)
        {
            var bounds = new Rect(BoxCreationPlacement.Find(preferred, size, desktop, occupied), size);
            Assert.True(desktop.Contains(bounds));
            foreach (var other in occupied)
                Assert.True(Rect.Intersect(bounds, other).IsEmpty);
            occupied.Add(bounds);
            preferred = new Point(bounds.Right + BoxCreationPlacement.Gap, bounds.Top);
        }
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040)]
    [InlineData(-1920, -300, 1920, 1040)]
    [InlineData(1280, 0, 1280, 680)]
    public void PlacementKeepsBoxesInsideTheChosenMonitor(double x, double y, double width, double height)
    {
        var desktop = new Rect(x, y, width, height);
        var size = new Size(340, 380);
        var existing = new Rect(desktop.TopLeft, size);
        var bounds = new Rect(BoxCreationPlacement.Find(existing.TopLeft, size, desktop, [existing]), size);
        Assert.True(desktop.Contains(bounds));
        Assert.True(Rect.Intersect(existing, bounds).IsEmpty);
    }

    [Fact]
    public void OccupiedOrderDoesNotChangeTheNewLocation()
    {
        var occupied = new[] { new Rect(20, 20, 340, 380), new Rect(380, 20, 340, 380) };
        var first = BoxCreationPlacement.Find(new Point(100, 100), new Size(340, 380), new Rect(0, 0, 1920, 1040), occupied);
        var reversed = BoxCreationPlacement.Find(new Point(100, 100), new Size(340, 380), new Rect(0, 0, 1920, 1040), occupied.Reverse());
        Assert.Equal(first, reversed);
    }

    [Fact]
    public void FullScreenUsesLeastOverlapAndOversizedBoxKeepsItsHeaderAccessible()
    {
        var size = new Size(340, 380);
        var desktop = new Rect(0, 0, 600, 400);
        var existing = new Rect(0, 0, 340, 380);
        var bounds = new Rect(BoxCreationPlacement.Find(new Point(), size, desktop, [existing]), size);
        Assert.True(desktop.Contains(bounds));
        Assert.True(Rect.Intersect(bounds, existing).Width < existing.Width);

        var oversized = BoxCreationPlacement.Find(new Point(999, 999), new Size(1400, 900), desktop, [existing]);
        Assert.Equal(desktop.TopLeft, oversized);
    }
}
