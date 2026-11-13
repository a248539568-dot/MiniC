using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MiniC.Models;
using MiniC.Services;
using Xunit;

namespace MiniC.Tests;

public sealed class AppearanceSwitchTests
{
    private static readonly string[] MaterialCycle = ["Crystal", "Frosted", "Solid"];
    [Theory]
    [InlineData("Crystal")]
    [InlineData("Frosted")]
    public async Task ThroughMenuIsAvailableForGlassAndPreservesOpticalParameters(string material)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var original = WindowAppearanceService.Normalize(new("Mint", material, 0.4,
                new MaterialPreferences { SeeThroughWindows = false, CrystalOpacity = 0.4, FrostedOpacity = 0.7,
                    FrostedBlurRadius = 31, RefractionStrength = 1.4, RefractionAngle = 42,
                    CrystalRippleStrength = 0.8, CrystalRippleScale = 2, Radius = 24 }));
            var current = original;
            var menu = new ContextMenu();
            AppearanceMenuBuilder.AddTo(menu, new(() => current, new Style(typeof(ContextMenu)),
                new Style(typeof(MenuItem)), new Style(typeof(Separator)), state => current = state), "颜色", "材质");
            var through = Assert.IsType<MenuItem>(menu.Items[3]);
            Assert.True(through.IsEnabled);
            Assert.True(through.IsCheckable);
            Assert.False(through.IsChecked);
            Assert.Equal("透出后方窗口", through.Header);
            through.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(through.IsChecked);
            Assert.Equal(original with { Appearance = original.Appearance with { SeeThroughWindows = true } }, current);
            foreach (var selected in MaterialCycle)
            {
                current = WindowAppearanceService.SetMaterial(current, selected);
                Assert.True(current.Appearance.SeeThroughWindows);
                Assert.Equal(original.Appearance with { SeeThroughWindows = true }, current.Appearance);
            }
            var restored = JsonSerializer.Deserialize<WindowAppearanceService.State>(JsonSerializer.Serialize(current))!;
            Assert.Equal(current, WindowAppearanceService.Normalize(restored));
            current = WindowAppearanceService.SetMaterial(current, material);
            through.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(current.Appearance.SeeThroughWindows);
            Assert.False(through.IsChecked);
        });
    }

    [Fact]
    public async Task SolidDisablesTransparencyWithoutDiscardingGlassSettings()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var glass = WindowAppearanceService.Normalize(new("White", "Crystal", 0.35,
                new MaterialPreferences { CrystalOpacity = 0.35, SeeThroughWindows = true }));
            var state = WindowAppearanceService.SetMaterial(glass, "Solid");
            Assert.Equal(1, state.Opacity);
            Assert.Equal(state, WindowAppearanceService.SetOpacity(state, 0));
            Assert.Equal(state, WindowAppearanceService.SetThrough(state, false));
            var menu = new ContextMenu();
            AppearanceMenuBuilder.AddTo(menu, new(() => state, new Style(typeof(ContextMenu)),
                new Style(typeof(MenuItem)), new Style(typeof(Separator)), value => state = value), "颜色", "材质");
            Assert.False(Assert.IsType<MenuItem>(menu.Items[2]).IsEnabled);
            var through = Assert.IsType<MenuItem>(menu.Items[3]);
            Assert.False(through.IsEnabled);
            Assert.False(through.IsChecked);
            var materials = Assert.IsType<MenuItem>(menu.Items[1]);
            materials.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "折射玻璃"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(glass, state);
            Assert.True(through.IsEnabled);
            Assert.True(through.IsChecked);
            Assert.True(Assert.IsType<MenuItem>(menu.Items[2]).IsEnabled);
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("Crystal", true, false, true)]
    [InlineData("Crystal", false, true, false)]
    [InlineData("Frosted", false, true, true)]
    [InlineData("Frosted", true, false, false)]
    [InlineData("Solid", true, true, false)]
    public void OldSourceFieldsMigrateUsingActiveMaterialAndAreNotWrittenAgain(string material, bool crystal, bool frosted, bool expected)
    {
        var legacy = JsonSerializer.Deserialize<MaterialPreferences>(
            $"{{\"CrystalSeeThrough\":{crystal.ToString().ToLowerInvariant()},\"FrostedSeeThrough\":{frosted.ToString().ToLowerInvariant()},\"RefractionStrength\":1.3}}")!;
        var state = WindowAppearanceService.Normalize(new("White", material, 0.62, legacy));
        Assert.Equal(expected, state.Appearance.SeeThroughWindows);
        Assert.Equal(1.3, state.Appearance.RefractionStrength);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(state.Appearance));
        Assert.False(json.RootElement.TryGetProperty("CrystalSeeThrough", out _));
        Assert.False(json.RootElement.TryGetProperty("FrostedSeeThrough", out _));
        Assert.Equal(expected, json.RootElement.GetProperty("SeeThroughWindows").GetBoolean());
        Assert.Equal(expected, WindowAppearanceService.SetMaterial(state, "Solid").Appearance.SeeThroughWindows);
        var explicitSource = WindowAppearanceService.Normalize(state with { Appearance = legacy with { SeeThroughWindows = !expected } });
        Assert.Equal(!expected, explicitSource.Appearance.SeeThroughWindows);
    }

    [Fact]
    public async Task MenuUsesLatestSettingsAfterSliderAndThroughChanges()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var state = WindowAppearanceService.Normalize(new("Blue", "Crystal", 0.62,
                new MaterialPreferences { RefractionStrength = 1.4, RefractionAngle = 42, FrostedOpacity = 0.7 }));
            var menu = new ContextMenu();
            AppearanceMenuBuilder.AddTo(menu, new(() => state, new Style(typeof(ContextMenu)),
                new Style(typeof(MenuItem)), new Style(typeof(Separator)), value => state = value), "颜色", "材质");
            var opacity = Assert.IsType<Grid>(Assert.IsType<MenuItem>(menu.Items[2]).Header);
            opacity.Children.OfType<Slider>().Single().Value = 0.8;
            Assert.Equal(0.2, state.Appearance.CrystalOpacity, 6);
            Assert.IsType<MenuItem>(menu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(state.Appearance.SeeThroughWindows);
            Assert.Equal(0.2, state.Appearance.CrystalOpacity, 6);
            var materials = Assert.IsType<MenuItem>(menu.Items[1]);
            materials.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "液态玻璃"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("Frosted", state.Material);
            Assert.True(state.Appearance.SeeThroughWindows);
            Assert.Equal(0.7, state.Opacity);
            materials.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "折射玻璃"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(0.2, state.Opacity, 6);
            Assert.True(state.Appearance.SeeThroughWindows);
            Assert.Equal(1.4, state.Appearance.RefractionStrength);
            Assert.Equal(42, state.Appearance.RefractionAngle);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task CaptureVisibilityIsASeparatePersistedMenuSetting()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var state = WindowAppearanceService.Normalize(new("White", "Crystal", 0.62,
                new MaterialPreferences { SeeThroughWindows = true, HideFromCapture = true }));
            var menu = new ContextMenu();
            AppearanceMenuBuilder.AddTo(menu, new(() => state, new Style(typeof(ContextMenu)),
                new Style(typeof(MenuItem)), new Style(typeof(Separator)), value => state = value), "颜色", "材质");

            var capture = Assert.IsType<MenuItem>(menu.Items[4]);
            Assert.Equal("截屏录屏时显示窗口", capture.Header);
            Assert.False(capture.IsChecked);
            capture.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(capture.IsChecked);
            Assert.False(state.Appearance.HideFromCapture);
            Assert.True(state.Appearance.SeeThroughWindows);
            Assert.Equal(1, state.Appearance.RefractionStrength);

            var restored = JsonSerializer.Deserialize<MaterialPreferences>(JsonSerializer.Serialize(state.Appearance))!;
            Assert.False(WindowAppearanceService.Normalize(state with { Appearance = restored }).Appearance.HideFromCapture);
            capture.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(state.Appearance.HideFromCapture);
            Assert.False(capture.IsChecked);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task NewBoxesShowOnCaptureAndOnlyUseDesktopByDefault()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var state = BoxAppearanceDefaults.CreateState();
            Assert.False(state.Appearance.HideFromCapture);
            Assert.False(state.Appearance.SeeThroughWindows);
            var menu = new ContextMenu();
            AppearanceMenuBuilder.AddTo(menu, new(() => state, new Style(typeof(ContextMenu)),
                new Style(typeof(MenuItem)), new Style(typeof(Separator)), value => state = value), "颜色", "材质");
            Assert.False(Assert.IsType<MenuItem>(menu.Items[3]).IsChecked);
            Assert.True(Assert.IsType<MenuItem>(menu.Items[4]).IsChecked);
            var saved = JsonSerializer.Deserialize<MaterialPreferences>(JsonSerializer.Serialize(state.Appearance))!;
            Assert.False(saved.HideFromCapture);
            Assert.False(saved.SeeThroughWindows);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void MaterialCycleAndHostReloadPreserveEachMaterialsPreferences()
    {
        var initial = WindowAppearanceService.Normalize(new("Mint", "Crystal", 0.4,
            new MaterialPreferences { CrystalOpacity = 0.4, FrostedOpacity = 0.7,
                CrystalSeeThrough = true, RefractionAngle = -70, RefractionStrength = 1.3,
                CrystalRippleScale = 1.8, CrystalRippleStrength = 0.6, Radius = 24 }));
        var state = initial;
        foreach (var material in new[] { "Frosted", "Solid", "Crystal" })
            state = WindowAppearanceService.SetMaterial(state, material);
        Assert.Equal(initial, state);
        var host = new BoxHostState { Theme = state.Theme, Material = state.Material,
            Opacity = state.Opacity, Appearance = state.Appearance };
        var restored = JsonSerializer.Deserialize<BoxHostState>(JsonSerializer.Serialize(host));
        Assert.Equal(initial, WindowAppearanceService.Normalize(new(restored.Theme, restored.Material,
            restored.Opacity.Value, restored.Appearance)));
    }
}
