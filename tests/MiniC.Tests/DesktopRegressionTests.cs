using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

public sealed class DesktopRegressionTests
{
    private static readonly Point ClickPoint = new(120, 80);

    [Fact]
    public void DoubleClickUsesPressTimeAndCanReleaseSecondClickLater()
    {
        var tracker = new DesktopItemOpenGestureTracker();
        tracker.RegisterMouseDown("a", ClickPoint, 1000);
        Assert.False(tracker.RegisterMouseUp("a"));
        tracker.RegisterMouseDown("a", ClickPoint, 1001);
        // 松开不再重新计算双击时限，按住第二次点击不使有效双击失效。
        Assert.True(tracker.RegisterMouseUp("a"));
        Assert.False(tracker.RegisterMouseUp("a"));
    }

    [Fact]
    public void InterruptedPressAndDragCannotOpenOrDragAnotherItem()
    {
        var tracker = new DesktopItemOpenGestureTracker();
        tracker.RegisterMouseDown("a", ClickPoint, 1000);
        Assert.True(tracker.IsPressed("a"));
        Assert.False(tracker.IsPressed("b"));
        tracker.RegisterMouseDown("a", ClickPoint, 1001);
        Assert.False(tracker.RegisterMouseUp("a"));
        tracker.RegisterMouseDown("a", ClickPoint, 1002);
        tracker.Reset();
        Assert.False(tracker.IsPressed("a"));
        Assert.False(tracker.RegisterMouseUp("a"));
    }

    [Fact]
    public void DifferentItemOrDistantOrExpiredClickDoesNotOpen()
    {
        foreach (var scenario in new[] { "different", "distant", "expired" })
        {
            var tracker = new DesktopItemOpenGestureTracker();
            tracker.RegisterMouseDown("a", ClickPoint, 1000);
            Assert.False(tracker.RegisterMouseUp("a"));
            var path = scenario == "different" ? "b" : "a";
            tracker.RegisterMouseDown(path, scenario == "distant" ? new Point(900, 900) : ClickPoint,
                scenario == "expired" ? 1001 + System.Windows.Forms.SystemInformation.DoubleClickTime : 1001);
            Assert.False(tracker.RegisterMouseUp(path));
        }
    }

    [Fact]
    public void DoubleClickSurvivesTimestampWraparound()
    {
        var tracker = new DesktopItemOpenGestureTracker();
        tracker.RegisterMouseDown("a", ClickPoint, int.MaxValue);
        Assert.False(tracker.RegisterMouseUp("a"));
        tracker.RegisterMouseDown("a", ClickPoint, int.MinValue);
        Assert.True(tracker.RegisterMouseUp("a"));
    }

    [Fact]
    public void MetadataRefreshPreservesTheClickedAndEditedItem()
    {
        var current = new DesktopItem
        {
            Path = "renamed.txt", Name = "old", LastWriteTicks = 1,
            IsSelected = true, IsRenaming = true, EditName = "draft", X = 180, Y = 250,
            ClipboardTransferState = ClipboardTransferState.Cut
        };
        var discovered = new DesktopItem { Path = "renamed.txt", Name = "renamed", LastWriteTicks = 2 };
        var result = DesktopItemRefreshService.Merge(current, discovered);
        Assert.Same(current, result);
        Assert.True(result.IsSelected);
        Assert.True(result.IsRenaming);
        Assert.Equal("draft", result.EditName);
        Assert.Equal(180, result.X);
        Assert.Equal(250, result.Y);
        Assert.Equal(ClipboardTransferState.Cut, result.ClipboardTransferState);
        Assert.Equal("renamed", result.Name);
        Assert.Equal(2, result.LastWriteTicks);
        Assert.True(result.NeedsIconRefresh);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040)] // 底部任务栏。
    [InlineData(0, 48, 1920, 1032)] // 顶部任务栏。
    [InlineData(64, 0, 1856, 1080)] // 左侧任务栏。
    [InlineData(0, 0, 1856, 1080)] // 右侧任务栏。
    public void AutoArrangeKeepsWholeTilesInsideWorkingArea(int x, int y, int width, int height)
    {
        var area = new Rectangle(x, y, width, height);
        var service = new DesktopIconPlacementService(() => [area]);
        var items = Items(60);
        var positions = items.ToDictionary(item => item.Path, _ => new DesktopIconPositionState { X = 0, Y = 1060 });
        service.AutoArrange(items, positions);
        AssertInside(positions, [area]);
        Assert.Equal(60, positions.Values.Select(position => (position.X, position.Y)).Distinct().Count());
    }

    [Fact]
    public void ExistingOffscreenPositionsAreRepairedWithoutNewFiles()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var service = new DesktopIconPlacementService(() => [area]);
        var items = Items(2);
        var positions = new Dictionary<string, DesktopIconPositionState>
        {
            ["0"] = new() { X = 16, Y = 16 },
            ["1"] = new() { X = 16, Y = 1020 }
        };
        Assert.True(service.PlaceMissing(items, positions, new Dictionary<string, System.Windows.Point>()));
        Assert.Equal(16, positions["0"].X);
        Assert.Equal(16, positions["0"].Y);
        AssertInside(positions, [area]);
        Assert.False(service.PlaceMissing(items, positions, new Dictionary<string, System.Windows.Point>()));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DragToTaskbarOrMonitorGapUsesAVisibleCell(double scale)
    {
        Rectangle[] areas = [new(-1280, 150, 1280, 834), new(0, 0, 1920, 1040)];
        var service = new DesktopIconPlacementService(() => areas, scale);
        var positions = new Dictionary<string, DesktopIconPositionState>();
        service.PlaceAt(["0"], new System.Windows.Point(-500, 10), [], positions);
        service.PlaceAt(["1"], new System.Windows.Point(1900, 1079), Items(1), positions);
        AssertInside(positions, areas, scale);
    }

    [Fact]
    public void MonitorWorkingAreaChangesRepairSavedPositions()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var service = new DesktopIconPlacementService(() => [area]);
        var positions = new Dictionary<string, DesktopIconPositionState> { ["0"] = new() { X = 16, Y = 984 } };
        area = new Rectangle(0, 0, 1920, 1000);
        Assert.True(service.PlaceMissing(Items(1), positions, new Dictionary<string, System.Windows.Point>()));
        AssertInside(positions, [area]);
    }

    private static DesktopItem[] Items(int count) => Enumerable.Range(0, count)
        .Select(index => new DesktopItem { Path = index.ToString(System.Globalization.CultureInfo.InvariantCulture), Name = "item" }).ToArray();

    private static void AssertInside(IDictionary<string, DesktopIconPositionState> positions,
        Rectangle[] areas, double scale = 1)
    {
        foreach (var position in positions.Values)
            Assert.Contains(areas, area => area.Contains(new Rectangle(position.X, position.Y,
                (int)Math.Ceiling(82 * scale), (int)Math.Ceiling(88 * scale))));
    }
}
