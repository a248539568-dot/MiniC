using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Models;
using System;
using System.Collections.Generic;
using Xunit;

namespace MiniC.Tests;

public sealed class DesktopBehaviorTests
{
    [Fact]
    public void ShortcutRenameAlwaysHidesLnkAndPreservesExtension()
    {
        var item = new DesktopItem
        {
            Path = @"C:\Temp\Microsoft Edge.lnk",
            Name = "Microsoft Edge",
            IsDirectory = false
        };

        Assert.Equal("Microsoft Edge", DesktopFileService.GetRenameEditName(item, extensionsVisible: true));
        Assert.Equal("Microsoft Edge New.lnk", DesktopFileService.GetRenameTargetFileName(
            item.Path, "Microsoft Edge New", isDirectory: false, extensionsVisible: true));
    }

    [Fact]
    public void OpenGestureSurvivesWindowActivationButRejectsDrag()
    {
        Assert.True(DesktopItemOpenGestureTracker.ValidateForSmokeTest());
    }

    [Fact]
    public void ClipboardTransferStateDistinguishesCopyAndCut()
    {
        Assert.True(ClipboardItemStateService.ValidateForSmokeTest());
    }

    [Fact]
    public void ZOrderFallbackNeverUsesGlobalTop()
    {
        Assert.True(DesktopLayerHostService.ValidateZOrderFallbackForSmokeTest());
    }

    [Fact]
    public void NewlyCreatedItemUsesGridCellUnderPointer()
    {
        const int originX = 16;
        const int originY = 16;
        var pointer = new System.Windows.Point(
            originX + DesktopIconPlacementService.TileWidth * 2 + DesktopIconPlacementService.TileWidth / 2,
            originY + DesktopIconPlacementService.TileHeight * 3 + DesktopIconPlacementService.TileHeight / 2);
        var positions = new Dictionary<string, DesktopIconPositionState>();

        new DesktopIconPlacementService(() => [new System.Drawing.Rectangle(0, 0, 1920, 1040)]).PlaceAt(
            ["new-item"], pointer, [], positions);

        Assert.Equal(originX + DesktopIconPlacementService.TileWidth * 2, positions["new-item"].X);
        Assert.Equal(originY + DesktopIconPlacementService.TileHeight * 3, positions["new-item"].Y);
    }
}
