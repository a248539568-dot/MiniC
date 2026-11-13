using MiniC.Models;
using MiniC.Services;
using MiniC.Widgets;
using Xunit;

namespace MiniC.Tests;

public sealed class BoxAppearanceDefaultsTests
{
    [Fact]
    public void InitializesLegacyHostsAndWidgetsWithoutChangingLayoutContent()
    {
        var group = new GroupState
        {
            Name = "保留名称", X = 120, Y = 240, Width = 520, Height = 460,
            Theme = "Purple", Material = "Frosted", Opacity = 0.2,
            ItemOrder = ["file-a"], TabGroupId = "host", TabOrder = 2
        };
        var host = new BoxHostState
        {
            Id = "host", Tabs = [new BoxTabRef(BoxContentKind.DesktopGroup, group.Id)],
            ActiveTab = new BoxTabRef(BoxContentKind.DesktopGroup, group.Id),
            X = 120, Y = 240, Width = 520, Height = 460,
            Theme = "Purple", Material = "Frosted", Opacity = 0.2,
            Appearance = new MaterialPreferences { Radius = 28, SeeThroughWindows = true }
        };
        var widget = new WidgetState
        {
            Title = "保留组件", X = 80, Y = 90, Width = 410, Height = 300,
            Theme = "Amber", Material = "Solid", Opacity = 0.1,
            Body = "保留内容"
        };
        var layout = new LayoutState { Groups = [group], BoxHosts = [host] };
        var persisted = 0;

        Assert.True(BoxAppearanceDefaults.Initialize(layout, [widget], _ => persisted++));
        Assert.Equal(1, persisted);
        Assert.Equal(BoxAppearanceDefaults.InitializationVersion, layout.AppearanceInitializationVersion);
        Assert.Equal(("White", "Crystal", 0.62), (group.Theme, group.Material, group.Opacity));
        Assert.Equal(("White", "Crystal", 0.62), (host.Theme, host.Material, host.Opacity));
        Assert.Equal(16, host.Appearance!.Radius);
        Assert.False(host.Appearance.SeeThroughWindows);
        Assert.Equal(("White", "Crystal", 0.62), (widget.Theme, widget.Material, widget.Opacity));
        Assert.Equal("保留名称", group.Name);
        Assert.Equal((120d, 240d, 520d, 460d), (group.X, group.Y, group.Width, group.Height));
        Assert.Equal(["file-a"], group.ItemOrder);
        Assert.Equal("保留内容", widget.Body);
        Assert.False(BoxAppearanceDefaults.Initialize(layout, [widget], _ => persisted++));
        Assert.Equal(1, persisted);
    }

    [Fact]
    public void UpdatesExistingCornersWithoutResettingMaterialOrContent()
    {
        var appearance = new MaterialPreferences
        {
            Radius = 8, SeeThroughWindows = true, HideFromCapture = false,
            CrystalOpacity = 0.4, RefractionStrength = 1.6
        };
        var host = new BoxHostState
        {
            Theme = "Mint", Material = "Frosted", Opacity = 0.73, Appearance = appearance
        };
        var widget = new WidgetState
        {
            Title = "保留天气", Theme = "Blue", Material = "Solid", Opacity = 0.8,
            Appearance = appearance, Body = "保留内容"
        };
        var layout = new LayoutState { AppearanceInitializationVersion = 1, BoxHosts = [host] };
        var saves = 0;
        Assert.True(BoxAppearanceDefaults.Initialize(layout, [widget], _ => saves++));
        Assert.Equal(appearance with { Radius = 16 }, host.Appearance);
        Assert.Equal(appearance with { Radius = 16 }, widget.Appearance);
        Assert.Equal(("Mint", "Frosted", 0.73), (host.Theme, host.Material, host.Opacity));
        Assert.Equal(("Blue", "Solid", 0.8, "保留内容"), (widget.Theme, widget.Material, widget.Opacity, widget.Body));
        Assert.Equal(1, saves);
        host.Appearance = host.Appearance with { Radius = 24 };
        Assert.False(BoxAppearanceDefaults.Initialize(layout, [widget], _ => saves++));
        Assert.Equal(24, host.Appearance.Radius);
        Assert.Equal(1, saves);
    }
}
