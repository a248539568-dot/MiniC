using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniC.Controls;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using Xunit;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;

namespace MiniC.Tests;

public sealed class CrystalMaskTests
{
    private static readonly string[] MaterialCycle = ["Crystal", "Frosted", "Solid", "Crystal"];
    private static readonly int[] TintSampleRows = [20, 100, 190];

    [Fact]
    public async Task WallpaperUsesTheCrystalShaderAndLiveSourceRemainsTransparent()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var source = Checker(256, 160);
            var surface = WallpaperRenderHost.Crystal(source);
            surface.Width = 256; surface.Height = 160; surface.Radius = 16; surface.Strength = 1;
            surface.Tint = Brushes.Transparent;
            Render(surface, new Size(256, 160));
            using var host = await WallpaperRenderHost.Load(surface, surface);
            var refracted = Render(surface, new Size(256, 160));
            Assert.True(DifferentPixels(source, refracted, new Int32Rect(50, 45, 150, 70)) > 500);
            Assert.True(DifferentPixels(source, refracted, new Int32Rect(2, 35, 14, 80)) > 30);
            Assert.Equal(0, Pixel(refracted, 0, 0)[3]);
            surface.UseWallpaper = false;
            Assert.Equal(new byte[4], Pixel(Render(surface, new Size(256, 160)), 128, 80));
            await Task.CompletedTask;
        });
    }
    [Theory]
    [InlineData(280, 220, 16, 1)]
    [InlineData(380, 300, 32, 1.5)]
    [InlineData(380, 300, 32, 2)]
    [InlineData(250, 42, 32, 1)]
    [InlineData(250, 170, 0, 1)]
    public async Task MaskKeepsRoundedCornersAndNeverFillsTheWideBand(int width, int height, double radius, double dpi)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var edge = new GlassSurfaceFrame { Width = width, Height = height, Radius = radius };
            var image = Render(edge, new Size(width, height), dpi);
            Assert.Equal(0, Pixel(image, (int)(width / 2 * dpi), (int)(height / 2 * dpi))[3]);
            if (radius > 0) Assert.Equal(0, Pixel(image, 0, 0)[3]);
            Assert.True(Pixel(image, (int)(dpi), (int)(height / 2 * dpi))[3] > 0);
            Assert.False(edge.IsHitTestVisible);
            var glass = WallpaperRenderHost.Crystal(Checker(width, height));
            glass.Width = width; glass.Height = height; glass.Radius = radius; glass.Tint = Brushes.Transparent;
            Render(glass, new Size(width, height), dpi);
            using var host = await WallpaperRenderHost.Load(glass, glass);
            var masked = Render(glass, new Size(width, height), dpi);
            Assert.NotNull(glass.OpacityMask);
            Assert.Equal(255, Pixel(masked, (int)(width / 2 * dpi), (int)(height / 2 * dpi))[3]);
            if (radius > 0) Assert.Equal(0, Pixel(masked, 0, 0)[3]);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void SwitchingSourcesIsIndependentOfNativeBackdropAvailabilityAndKeepsSettings()
    {
        var group = new DeskGroup { Material = "Crystal", Theme = "Mint", Appearance = new MaterialPreferences
            { Radius = 24, RefractionAngle = 65, RefractionStrength = 1.3, CrystalRippleStrength = 0.8 } };
        var tint = ((SolidColorBrush)group.GlassBackground).Color;
        Assert.True(group.UsesWallpaper);
        group.Appearance = group.Appearance with { SeeThroughWindows = true };
        Assert.False(group.UsesWallpaper);
        group.ComposedBackdropActive = true;
        Assert.False(group.UsesWallpaper);
        group.Material = "Solid";
        Assert.False(group.UsesWallpaper);
        group.Material = "Frosted";
        Assert.False(group.UsesWallpaper);
        group.Material = "Crystal";
        Assert.False(group.UsesWallpaper);
        group.Appearance = group.Appearance with { SeeThroughWindows = false };
        Assert.True(group.UsesWallpaper);
        Assert.Equal(tint, ((SolidColorBrush)group.GlassBackground).Color);
        Assert.Equal(65, group.Appearance.RefractionAngle);
        Assert.Equal(1.3, group.Appearance.RefractionStrength);
        Assert.Equal(0.8, group.Appearance.CrystalRippleStrength);
    }

    [Fact]
    public async Task ProductionShellKeepsTintUniformAndForegroundOutsideTheMaskAcrossMaterialCycles()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var group = new DeskGroup { Material = "Crystal", Theme = "Blue", Width = 280, Height = 220,
                Appearance = new MaterialPreferences { CrystalSeeThrough = true, Radius = 16 } };
            var callbacks = new DeskGroupWindowCallbacks(_ => { }, (_, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.FromResult(false), (_, _, _, _) => Task.CompletedTask,
                _ => { }, _ => { }, _ => { }, _ => { }, () => { });
            var window = new DeskGroupWindow(group, callbacks, new DesktopLayerHostService(), new DesktopDragStateService());
            try
            {
                var shell = (Border)window.Content;
                shell.Measure(new Size(280, 220));
                shell.Arrange(new Rect(0, 0, 280, 220));
                shell.UpdateLayout();
                var material = (Grid)window.FindName("GroupMaterial");
                var wallpaper = (CrystalGlassSurface)window.FindName("CrystalBackdrop");
                var tint = ((SolidColorBrush)group.GlassBackground).Color;
                foreach (var mode in MaterialCycle)
                {
                    group.Material = mode;
                    shell.UpdateLayout();
                    if (mode != "Crystal") continue;
                    Assert.Equal(Visibility.Visible, wallpaper.Visibility);
                    Assert.Equal(tint, ((SolidColorBrush)group.GlassBackground).Color);
                    var pixels = Render(material, new Size(280, 220));
                    foreach (var y in TintSampleRows)
                        Assert.Equal(tint.A, Pixel(pixels, 140, y)[3]);
                    Assert.Equal(0, Pixel(pixels, 0, 0)[3]);
                }
                Assert.Null(shell.OpacityMask);
                Assert.Null(material.OpacityMask);
                Assert.Equal(1, shell.Opacity);
                Assert.Null(((Border)window.FindName("GroupHeader")).OpacityMask);
                group.Appearance = group.Appearance with { SeeThroughWindows = false };
                shell.UpdateLayout();
                Assert.Equal(Visibility.Visible, wallpaper.Visibility);
                group.Appearance = group.Appearance with { SeeThroughWindows = true, Radius = 32 };
                shell.UpdateLayout();
                Assert.Equal(Visibility.Visible, wallpaper.Visibility);
                Assert.Equal(32, Assert.IsType<RectangleGeometry>(material.Clip).RadiusX);
                Assert.False(window.IsLoaded);
                Assert.False(window.IsVisible);
                await Task.CompletedTask;
            }
            finally { window.ClosePermanently(); }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task BothPreviewModesReuseTheMaskedGlassAndSnapshotOnlyForeground(double radius)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var group = new DeskGroup { Material = "Crystal", Appearance = new MaterialPreferences { CrystalSeeThrough = true, Radius = radius } };
            var material = new Grid();
            var wallpaper = new CrystalGlassSurface { Tint = group.GlassBackground, Radius = radius };
            wallpaper.SetBinding(CrystalGlassSurface.UseWallpaperProperty, new System.Windows.Data.Binding(nameof(DeskGroup.UsesWallpaper))
                { Source = group });
            wallpaper.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(DeskGroup.IsCrystal))
                { Source = group, Converter = new BooleanToVisibilityConverter() });
            material.Children.Add(wallpaper);
            var shell = new Border { Width = 280, Height = 220, CornerRadius = group.Corners, Child = material };
            Render(shell, new Size(280, 220));
            var live = new GroupDragPreviewWindow(shell, new Rect(0, 0, 280, 220), Brushes.Transparent, material: material);
            try
            {
                var layers = Assert.IsType<Grid>(live.Content);
                var surface = layers.Children.OfType<GroupTransitionSurface>().Single();
                var glass = layers.Children.OfType<Canvas>().Single().Children.OfType<CrystalGlassSurface>().Single();
                Assert.False(glass.UseWallpaper);
                Assert.Equal(radius, glass.Radius);
                Assert.Equal(radius, surface.Radius);
                surface.PreviewScale = 0.5;
                Assert.Equal(radius * 0.5, surface.CardRadius);
                Assert.Equal(0, Pixel((BitmapSource)surface.Snapshot, 140, 100)[3]);
                Assert.Equal(Visibility.Visible, wallpaper.Visibility);
                Assert.False(live.IsVisible);
            }
            finally { live.Close(); }
            group.Appearance = group.Appearance with { SeeThroughWindows = false };
            Render(shell, new Size(280, 220));
            var desktop = new GroupDragPreviewWindow(shell, new Rect(0, 0, 280, 220), Brushes.Transparent, material: material);
            try
            {
                Assert.IsType<Grid>(desktop.Content);
                Assert.Equal(Visibility.Visible, wallpaper.Visibility);
                Assert.True(System.Windows.Data.BindingOperations.IsDataBound(wallpaper, UIElement.VisibilityProperty));
                group.Appearance = group.Appearance with { SeeThroughWindows = true };
                Assert.Equal(Visibility.Visible, wallpaper.Visibility);
                Assert.False(wallpaper.UseWallpaper);
                Assert.False(desktop.IsVisible);
            }
            finally { desktop.Close(); }
            await Task.CompletedTask;
        });
    }

    private static RenderTargetBitmap Render(FrameworkElement element, Size size, double scale = 1)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        var image = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(element);
        return image;
    }

    private static RenderTargetBitmap Render(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        return image;
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static BitmapSource Checker(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var value = (byte)((x / 6 + y / 6) % 2 == 0 ? 220 : 35);
                var offset = (y * width + x) * 4;
                pixels[offset] = value; pixels[offset + 1] = value; pixels[offset + 2] = value; pixels[offset + 3] = 255;
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static int DifferentPixels(BitmapSource first, BitmapSource second, Int32Rect region)
    {
        var a = new byte[region.Width * region.Height * 4];
        var b = new byte[a.Length];
        first.CopyPixels(region, a, region.Width * 4, 0);
        second.CopyPixels(region, b, region.Width * 4, 0);
        return Enumerable.Range(0, a.Length / 4).Count(i => Math.Abs(a[i * 4 + 1] - b[i * 4 + 1]) > 4);
    }
}
