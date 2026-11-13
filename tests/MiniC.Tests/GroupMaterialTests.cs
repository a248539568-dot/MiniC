using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Media;
using MiniC.Models;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

public sealed class GroupMaterialTests
{
    [Theory]
    [InlineData("White")]
    [InlineData("Blue")]
    [InlineData("Mint")]
    [InlineData("Purple")]
    [InlineData("Amber")]
    [InlineData("Graphite")]
    public void LiveCrystalKeepsTheChosenThemeInsteadOfTurningItIntoASmokedPanel(string theme)
    {
        var group = new DeskGroup { Material = "Crystal", Theme = theme };
        var wallpaperTint = Assert.IsType<SolidColorBrush>(group.GlassBackground).Color;
        group.Appearance = group.Appearance with { SeeThroughWindows = true };
        var liveTint = Assert.IsType<SolidColorBrush>(group.GlassBackground).Color;
        Assert.Equal(wallpaperTint.A, liveTint.A);
        Assert.InRange(liveTint.A, (byte)1, (byte)20); // A subtle tint, without a white/smoked fill.
        Assert.Equal(wallpaperTint.R, liveTint.R);
        Assert.Equal(wallpaperTint.G, liveTint.G);
        Assert.Equal(wallpaperTint.B, liveTint.B);
        Assert.True(group.ContentShadow.IsFrozen);
        group.Opacity = 0;
        Assert.Equal(1, Assert.IsType<SolidColorBrush>(group.GlassBackground).Color.A);
        group.Opacity = 1;
        Assert.InRange(Assert.IsType<SolidColorBrush>(group.GlassBackground).Color.A, (byte)20, (byte)26);
        group.Appearance = group.Appearance with { SeeThroughWindows = false };
        group.Opacity = 0.62;
        Assert.Equal(wallpaperTint, Assert.IsType<SolidColorBrush>(group.GlassBackground).Color);
    }

    [Theory]
    [InlineData("Solid")]
    [InlineData("Frosted")]
    [InlineData("Crystal")]
    public void ThemeUpdatesChangeColorWithoutResettingMaterialOrOpacity(string material)
    {
        var group = new DeskGroup { Material = material, Theme = "Blue", Opacity = 0.7 };
        var notifications = new HashSet<string>();
        group.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var blue = Assert.IsType<SolidColorBrush>(group.GlassBackground).Color;
        group.Theme = "Amber";
        var amber = Assert.IsType<SolidColorBrush>(group.GlassBackground).Color;
        Assert.True(System.Math.Abs(blue.R - amber.R) > 90);
        Assert.True(System.Math.Abs(blue.B - amber.B) > 90);
        Assert.Equal(blue.A, amber.A);
        Assert.Equal(material, group.Material);
        Assert.Equal(material == "Solid" ? 1 : 0.7, group.Opacity);
        Assert.Contains(nameof(group.GlassBackground), notifications);
    }
    [Theory]
    [InlineData("Solid")]
    [InlineData("Crystal")]
    [InlineData("Frosted")]
    public void MaterialsKeepThemeOpacityAndPersist(string material)
    {
        var group = new DeskGroup { Theme = "Blue", Material = material, Opacity = 0.44 };
        var first = group.GlassBackground;
        Assert.True(first.IsFrozen);
        if (material == "Crystal")
        {
            Assert.True(group.IsCrystal);
        }
        else Assert.IsType<SolidColorBrush>(first);
        group.Opacity = 0.88;
        var second = group.GlassBackground;
        static byte Alpha(Brush brush) => ((SolidColorBrush)brush).Color.A;
        if (material == "Solid") Assert.Equal(255, Alpha(second));
        else Assert.True(Alpha(second) > Alpha(first));
        group.Theme = "Graphite";
        static Color Tint(Brush brush) => ((SolidColorBrush)brush).Color;
        Assert.NotEqual(Tint(first).R, Tint(group.GlassBackground).R);
        var json = JsonSerializer.Serialize(new GroupState { Theme = group.Theme, Material = material, Opacity = group.Opacity });
        var restored = JsonSerializer.Deserialize<GroupState>(json);
        Assert.Equal(material, restored.Material);
        Assert.Equal(material == "Solid" ? 1 : 0.88, restored.Opacity);
    }

    [Theory]
    [InlineData("Solid", "White")]
    [InlineData("Frosted", "Blue")]
    [InlineData("Crystal", "Graphite")]
    public void MaterialTextAndControlsShareWhiteForeground(string material, string theme)
    {
        var group = new DeskGroup { Material = material, Theme = theme };
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(group.PrimaryForeground).Color);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(group.CountForeground).Color);
        var secondary = Assert.IsType<SolidColorBrush>(group.SecondaryForeground).Color;
        Assert.Equal((byte)255, secondary.R);
        Assert.Equal((byte)255, secondary.G);
        Assert.Equal((byte)255, secondary.B);
        Assert.True(Assert.IsType<SolidColorBrush>(group.GlassMenuBackground).Color.A > 200);
        Assert.True(Assert.IsType<SolidColorBrush>(group.CountBackground).Color.A > 100);
    }
    [Fact]
    public void SwitchingMaterialUpdatesEveryVisualLayerAndLegacyDefaultsRemain()
    {
        var group = new DeskGroup { Material = "Frosted" };
        var changed = new HashSet<string>();
        group.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        group.Material = "Solid";
        Assert.Contains(nameof(group.GlassBackground), changed);
        Assert.Contains(nameof(group.HeaderBackground), changed);
        Assert.Contains(nameof(group.IsCrystal), changed);
        Assert.Contains(nameof(group.IsSolid), changed);
        Assert.False(group.IsCrystal);
        Assert.Equal((byte)0, Assert.IsType<SolidColorBrush>(group.HeaderBackground).Color.A);
        Assert.Equal("Crystal", JsonSerializer.Deserialize<GroupState>("{}").Material);
        group.Material = "Clear";
        Assert.False(group.IsCrystal);
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(group.GlassBackground).Color.A);
    }

    [Fact]
    public void GlassLayersRemainVisibleWhenBackgroundSourceChanges()
    {
        var frosted = new DeskGroup { Material = "Frosted", ComposedBackdropActive = true };
        var nativeTint = ((SolidColorBrush)frosted.GlassBackground).Color;
        frosted.ComposedBackdropActive = false;
        Assert.Equal(nativeTint, ((SolidColorBrush)frosted.GlassBackground).Color);
        Assert.True(nativeTint.A > 0);

        var crystal = new DeskGroup
        {
            Material = "Crystal",
            Appearance = new MiniC.Models.MaterialPreferences { CrystalSeeThrough = true }
        };
        Assert.True(crystal.IsCrystal);
        Assert.False(crystal.UsesWallpaper);
        var tint = ((SolidColorBrush)crystal.GlassBackground).Color;
        crystal.ComposedBackdropActive = true;
        Assert.Equal(tint, ((SolidColorBrush)crystal.GlassBackground).Color);
        Assert.True(crystal.IsCrystalSeeThrough);
        crystal.ComposedBackdropActive = false;
        Assert.Equal(tint, ((SolidColorBrush)crystal.GlassBackground).Color);
    }

    [Fact]
    public void CrystalKeepsTheSameGlassLayerWhenSeeThroughToggles()
    {
        var group = new DeskGroup
        {
            Material = "Crystal",
            Appearance = new MaterialPreferences { CrystalSeeThrough = false }
        };
        var wallpaperModeTint = ((SolidColorBrush)group.GlassBackground).Color;
        group.Appearance = group.Appearance with { SeeThroughWindows = true };

        Assert.False(group.UsesWallpaper);
        Assert.Equal(wallpaperModeTint, ((SolidColorBrush)group.GlassBackground).Color);
        Assert.True(group.IsCrystal);
    }

    [Fact]
    public void AppearanceChangesRefreshAllMaterialLayers()
    {
        var group = new DeskGroup { Material = "Crystal" };
        var changed = new HashSet<string>();
        group.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        group.Appearance = group.Appearance with { SeeThroughWindows = true, Radius = 24 };

        Assert.Contains(nameof(group.Corners), changed);
        Assert.Contains(nameof(group.UsesWallpaper), changed);
        Assert.Contains(nameof(group.IsCrystalSeeThrough), changed);
        Assert.Contains(nameof(group.GlassBackground), changed);
        Assert.True(group.IsCrystal);
    }

    [Fact]
    public void MaterialPreferencesKeepIndependentMaterialTracks()
    {
        var preferences = new MaterialPreferences
        {
            FrostedSeeThrough = true,
            FrostedBlurRadius = 27,
            FrostedOpacity = 0.31,
            CrystalSeeThrough = false,
            CrystalRippleStrength = 0.84,
            CrystalRippleScale = 2.2,
            CrystalOpacity = 0.73
        }.Normalize();

        Assert.True(preferences.FrostedSeeThrough);
        Assert.Equal(27, preferences.FrostedBlurRadius);
        Assert.Equal(0.31, preferences.FrostedOpacity);
        Assert.False(preferences.CrystalSeeThrough);
        Assert.Equal(0.84, preferences.CrystalRippleStrength);
        Assert.Equal(2.2, preferences.CrystalRippleScale);
        Assert.Equal(0.73, preferences.CrystalOpacity);
        Assert.Equal(1, preferences.OpacityFor("Solid"));
    }

    [Fact]
    public void FrostedSeeThroughDoesNotChangeOtherMaterialSettings()
    {
        var group = new DeskGroup { Material = "Frosted", Opacity = 0.58 };
        var original = group.Appearance with
        {
            CrystalSeeThrough = true, CrystalRippleStrength = 1.1,
            CrystalOpacity = 0.37
        };
        group.Appearance = original;
        Assert.False(group.ComposedBackdropActive);
        var tint = ((SolidColorBrush)group.GlassBackground).Color;
        group.Appearance = group.Appearance with { SeeThroughWindows = true };
        group.ComposedBackdropActive = true;
        Assert.True(group.ComposedBackdropActive);
        Assert.Equal(tint, ((SolidColorBrush)group.GlassBackground).Color);
        Assert.True(group.Appearance.SeeThroughWindows);
        Assert.False(group.Appearance.CrystalSeeThrough);
        Assert.Equal(original.CrystalRippleStrength, group.Appearance.CrystalRippleStrength);
        Assert.Equal(original.CrystalOpacity, group.Appearance.CrystalOpacity);
        Assert.Equal(1, group.Appearance.OpacityFor("Solid"));
    }

    [Fact]
    public void FrostedThroughIsOptInAndIndependentOfMaterialSelection()
    {
        var group = new DeskGroup { Material = "Frosted" };
        Assert.False(group.Appearance.FrostedSeeThrough);
        Assert.False(group.IsFrostedSeeThrough);
        group.Appearance = group.Appearance with { SeeThroughWindows = true };
        Assert.True(group.IsFrostedSeeThrough);
        Assert.False(group.ComposedBackdropActive);
        group.Material = "Crystal";
        Assert.False(group.IsFrosted);
        Assert.False(group.IsFrostedSeeThrough);
    }

    [Theory]
    [InlineData("Clear", "Solid")]
    [InlineData("Satin", "Solid")]
    [InlineData("Gradient", "Solid")]
    [InlineData("Unknown", "Solid")]
    [InlineData("Crystal", "Crystal")]
    [InlineData("Frosted", "Frosted")]
    public void RemovedMaterialsNormalizeToSupportedValues(string old, string expected)
    {
        var group = new DeskGroup { Material = old };
        Assert.Equal(expected, group.Material);
        Assert.Equal(3, MiniC.Services.GroupMaterialBrushes.Choices.Count);
    }

}

