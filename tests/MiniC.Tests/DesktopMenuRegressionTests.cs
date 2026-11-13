using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using MiniC.Controllers;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

public sealed class DesktopMenuRegressionTests
{
    [Fact]
    public void NativeDesktopSettingsRoundTripWithDefaultsAndCustomValues()
    {
        var defaults = new NativeDesktopState();
        Assert.Equal("Medium", defaults.ViewMode);
        Assert.Equal("Name", defaults.SortMode);
        Assert.True(defaults.SortAscending);
        Assert.Equal("None", defaults.GroupMode);
        Assert.True(defaults.GroupAscending);
        Assert.False(defaults.AutoArrange);
        Assert.True(defaults.AlignToGrid);

        var source = new NativeDesktopState
        {
            ViewMode = "Large",
            SortMode = "Date",
            SortAscending = false,
            GroupMode = "Type",
            GroupAscending = false,
            AutoArrange = true,
            AlignToGrid = false
        };
        var restored = JsonSerializer.Deserialize<NativeDesktopState>(JsonSerializer.Serialize(source));
        Assert.NotNull(restored);
        Assert.Equal(source.ViewMode, restored!.ViewMode);
        Assert.Equal(source.SortMode, restored.SortMode);
        Assert.Equal(source.SortAscending, restored.SortAscending);
        Assert.Equal(source.GroupMode, restored.GroupMode);
        Assert.Equal(source.GroupAscending, restored.GroupAscending);
        Assert.Equal(source.AutoArrange, restored.AutoArrange);
        Assert.Equal(source.AlignToGrid, restored.AlignToGrid);
    }

    [Fact]
    public void DesktopLayoutCommandsMapToIndependentApplicationResults()
    {
        var expected = new Dictionary<uint, ShellContextMenuResult>
        {
            [0x7101] = ShellContextMenuResult.DesktopViewSmall,
            [0x7102] = ShellContextMenuResult.DesktopViewMedium,
            [0x7103] = ShellContextMenuResult.DesktopViewLarge,
            [0x7110] = ShellContextMenuResult.DesktopSortName,
            [0x7111] = ShellContextMenuResult.DesktopSortSize,
            [0x7112] = ShellContextMenuResult.DesktopSortType,
            [0x7113] = ShellContextMenuResult.DesktopSortDate,
            [0x7114] = ShellContextMenuResult.DesktopSortAscending,
            [0x7115] = ShellContextMenuResult.DesktopSortDescending,
            [0x7120] = ShellContextMenuResult.DesktopGroupNone,
            [0x7121] = ShellContextMenuResult.DesktopGroupName,
            [0x7122] = ShellContextMenuResult.DesktopGroupSize,
            [0x7123] = ShellContextMenuResult.DesktopGroupType,
            [0x7124] = ShellContextMenuResult.DesktopGroupDate,
            [0x7125] = ShellContextMenuResult.DesktopGroupAscending,
            [0x7126] = ShellContextMenuResult.DesktopGroupDescending,
            [0x7130] = ShellContextMenuResult.DesktopAutoArrange,
            [0x7131] = ShellContextMenuResult.DesktopAlignGrid
        };

        foreach (var pair in expected)
        {
            Assert.True(ShellContextMenuService.TryMapDesktopLayoutCommand(pair.Key, out var result));
            Assert.Equal(pair.Value, result);
        }

        Assert.False(ShellContextMenuService.TryMapDesktopLayoutCommand(0x7FFF, out var unknown));
        Assert.Equal(ShellContextMenuResult.Failed, unknown);
    }

    [Fact]
    public void DesktopOrderingUsesGroupAndSortDirectionsWithoutMixedKeyComparison()
    {
        var items = new[]
        {
            new DesktopItem { Path = "z.txt", Name = "Zulu", LastWriteTicks = 10 },
            new DesktopItem { Path = "a.exe", Name = "Alpha", LastWriteTicks = 20 },
            new DesktopItem { Path = "b.txt", Name = "Bravo", LastWriteTicks = 30 }
        };
        var state = new NativeDesktopState
        {
            GroupMode = "Type",
            GroupAscending = true,
            SortMode = "Name",
            SortAscending = false
        };

        var ordered = DesktopCoordinator.OrderDesktopItems(items, state);
        Assert.Equal(["a.exe", "z.txt", "b.txt"], ordered.Select(item => item.Path));
    }

    [Fact]
    public void SnapToGridRepairsOverlappingPositionsUsingFixedCells()
    {
        var items = new[]
        {
            new DesktopItem { Path = "a", Name = "A" },
            new DesktopItem { Path = "b", Name = "B" }
        };
        var positions = new Dictionary<string, DesktopIconPositionState>
        {
            ["a"] = new() { X = 18, Y = 17 },
            ["b"] = new() { X = 18, Y = 17 }
        };
        var service = new DesktopIconPlacementService(
            () => [new System.Drawing.Rectangle(0, 0, 1920, 1040)]);

        Assert.True(service.SnapToGrid(items, positions));
        Assert.Equal((16, 16), (positions["a"].X, positions["a"].Y));
        Assert.Equal((16 + DesktopIconPlacementService.TileWidth, 16),
            (positions["b"].X, positions["b"].Y));
    }
}
