using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MiniC.Controls;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using Xunit;
using Size = System.Windows.Size;

namespace MiniC.Tests;

public sealed class FrostedGlassTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public async Task DiffusionRemovesSmallDetailsPreservesColourAndDoesNotFadeTheRim(double scale)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var texture = Texture();
            var surface = WallpaperRenderHost.Frosted(texture);
            surface.Width = 320; surface.Height = 220; surface.Radius = 24;
            surface.Tint = Brushes.Transparent; surface.BlurRadius = 24;
            Render(surface);
            using var host = await WallpaperRenderHost.Load(surface, surface);
            var sharpSurface = WallpaperRenderHost.Frosted(texture);
            sharpSurface.Width = 320; sharpSurface.Height = 220; sharpSurface.Radius = 24;
            sharpSurface.Tint = Brushes.Transparent; sharpSurface.BlurRadius = 0;
            Render(sharpSurface);
            using var sharpHost = await WallpaperRenderHost.Load(sharpSurface, sharpSurface);
            var sharp = Render(sharpSurface, scale);
            var soft = Render(surface, scale);
            Assert.True(Detail(soft, scale) < Detail(sharp, scale) * 0.2);
            var left = Pixel(soft, (int)(60 * scale), (int)(110 * scale));
            var right = Pixel(soft, (int)(260 * scale), (int)(110 * scale));
            Assert.True(left[2] - left[0] > 50);
            Assert.True(right[0] - right[2] > 50);
            Assert.Equal(255, left[3]);
            Assert.Equal(255, Pixel(soft, (int)(5 * scale), (int)(110 * scale))[3]);
            Assert.Equal(0, Pixel(soft, 0, 0)[3]);
            Assert.NotNull(surface.OpacityMask);
            Assert.IsType<BlurEffect>(surface.Children.OfType<WallpaperGlassSurface>().Single().Effect);
            Export($"frosted-source-{scale}", sharp);
            Export($"frosted-diffusion-{scale}", soft);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task NativeSourceSwitchPreservesTintMaskAndExcludesForegroundFromBlur()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var tint = GroupMaterialBrushes.Background("White", "Frosted", 0.62);
            var glass = WallpaperRenderHost.Frosted(Texture());
            glass.Width = 320; glass.Height = 220; glass.Radius = 24; glass.Tint = tint;
            Render(glass);
            using var host = await WallpaperRenderHost.Load(glass, glass);
            var source = glass.Children.OfType<WallpaperGlassSurface>().Single();
            var mask = glass.OpacityMask;
            glass.NativeBackdropActive = true;
            var nativeOverlay = Render(glass);
            Assert.Equal(Visibility.Hidden, source.Visibility);
            Assert.Equal(((SolidColorBrush)tint).Color.A, Pixel(nativeOverlay, 160, 110)[3]);
            Assert.Same(mask, glass.OpacityMask);
            glass.BlurRadius = 35;
            Assert.Equal(35, Assert.IsType<BlurEffect>(source.Effect).Radius);
            glass.NativeBackdropActive = false;
            var desktop = Render(glass);
            Assert.Equal(Visibility.Visible, source.Visibility);
            Assert.Equal(255, Pixel(desktop, 160, 110)[3]);
            Assert.Same(mask, glass.OpacityMask);
            Assert.Null(glass.Effect);
            host.Dispose();
            var foreground = new Border { Width = 8, Height = 8, Background = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var scene = new Grid { Width = 320, Height = 220 };
            glass.Width = double.NaN; glass.Height = double.NaN;
            scene.Children.Add(glass); scene.Children.Add(foreground);
            var image = Render(scene);
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(image, 159, 109));
            Assert.Null(foreground.Effect);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ProductionShellBindsFrostedLayersAndSwitchesMaterialsWithoutDuplicatingTint()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var group = new DeskGroup { Material = "Frosted" };
            var callbacks = new DeskGroupWindowCallbacks(_ => { }, (_, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.FromResult(false), (_, _, _, _) => Task.CompletedTask,
                _ => { }, _ => { }, _ => { }, _ => { }, () => { });
            var window = new DeskGroupWindow(group, callbacks, new DesktopLayerHostService(), new DesktopDragStateService());
            try
            {
                Render((FrameworkElement)window.Content);
                var material = (Grid)window.FindName("GroupMaterial");
                var frosted = Assert.IsType<FrostedGlassSurface>(window.FindName("FrostedBackdrop"));
                var crystal = Assert.IsType<CrystalGlassSurface>(window.FindName("CrystalBackdrop"));
                var solid = Assert.IsType<SolidMaterialSurface>(window.FindName("SolidBackdrop"));
                Assert.Equal(Visibility.Visible, frosted.Visibility);
                Assert.True(frosted.UseWallpaper);
                Assert.Equal(Visibility.Collapsed, solid.Visibility);
                Assert.Equal(((SolidColorBrush)group.GlassBackground).Color,
                    ((SolidColorBrush)frosted.Tint).Color);
                group.ComposedBackdropActive = true;
                Assert.True(frosted.NativeBackdropActive);
                Assert.Equal(Visibility.Visible, frosted.Visibility);
                group.Material = "Solid";
                Assert.Equal(Visibility.Collapsed, frosted.Visibility);
                Assert.Equal(Visibility.Visible, solid.Visibility);
                group.Material = "Crystal";
                Assert.Equal(Visibility.Visible, crystal.Visibility);
                Assert.Equal(Visibility.Collapsed, solid.Visibility);
                group.ComposedBackdropActive = false;
                group.Material = "Frosted";
                Assert.False(frosted.NativeBackdropActive);
                group.Appearance = group.Appearance with { SeeThroughWindows = true };
                Assert.False(frosted.UseWallpaper);
                group.ComposedBackdropActive = true;
                Assert.False(frosted.UseWallpaper);
                group.ComposedBackdropActive = false;
                Assert.False(frosted.UseWallpaper);
                group.Material = "Crystal";
                Assert.False(crystal.UseWallpaper);
                group.Appearance = group.Appearance with { SeeThroughWindows = true };
                Assert.False(crystal.UseWallpaper);
                group.Appearance = group.Appearance with { SeeThroughWindows = false };
                Assert.True(crystal.UseWallpaper);
                group.Material = "Solid";
                Assert.False(solid.UseWallpaper);
                var solidTint = ((SolidColorBrush)solid.Tint).Color;
                group.Appearance = group.Appearance with { SeeThroughWindows = true };
                Assert.False(solid.UseWallpaper);
                Assert.Equal(solidTint, ((SolidColorBrush)solid.Tint).Color);
                var preview = Assert.IsType<SolidMaterialSurface>(solid.CreatePreview());
                Assert.False(preview.UseWallpaper);
                Assert.Same(solid.Tint, preview.Tint);
                Assert.Null(((FrameworkElement)window.FindName("GroupHeader")).Effect);
                Assert.False(window.IsVisible);
            }
            finally { window.ClosePermanently(); }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FrostedPreviewRecreatesMaterialAndLeavesOnlyForegroundInSnapshot()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var glass = new FrostedGlassSurface { BlurRadius = 29, Radius = 20,
                Tint = GroupMaterialBrushes.Background("Mint", "Frosted", 0.5) };
            var material = new Grid(); material.Children.Add(glass);
            var shell = new Border { Width = 320, Height = 220, CornerRadius = new CornerRadius(20), Child = material };
            Render(shell);
            var preview = new GroupDragPreviewWindow(shell, new Rect(0, 0, 320, 220), Brushes.Transparent, material: material);
            try
            {
                var layers = Assert.IsType<Grid>(preview.Content);
                var cloned = Assert.IsType<FrostedGlassSurface>(layers.Children.OfType<Canvas>().Single().Children[0]);
                Assert.False(cloned.UseWallpaper);
                Assert.Equal(29, cloned.BlurRadius);
                Assert.Equal(20, cloned.Radius);
                Assert.Same(glass.Tint, cloned.Tint);
                var content = layers.Children.OfType<GroupTransitionSurface>().Single();
                Assert.Equal(0, Pixel((BitmapSource)content.Snapshot, 160, 110)[3]);
                Assert.Equal(Visibility.Visible, glass.Visibility);
                Assert.False(preview.IsVisible);
            }
            finally { preview.Close(); }
            await Task.CompletedTask;
        });
    }

    internal static BitmapSource Texture()
    {
        var pixels = new byte[320 * 220 * 4];
        for (var y = 0; y < 220; y++)
            for (var x = 0; x < 320; x++)
            {
                var i = (y * 320 + x) * 4;
                var delta = (x / 2 + y / 2) % 2 == 0 ? 35 : -35;
                pixels[i] = (byte)((x < 160 ? 90 : 210) + delta);
                pixels[i + 1] = (byte)(145 + delta);
                pixels[i + 2] = (byte)((x < 160 ? 210 : 90) + delta);
                pixels[i + 3] = 255;
            }
        var source = BitmapSource.Create(320, 220, 96, 96, PixelFormats.Pbgra32, null, pixels, 320 * 4);
        source.Freeze();
        return source;
    }

    private static double Detail(BitmapSource image, double scale) => Enumerable.Range(45, 60)
        .Average(x => Math.Abs(Pixel(image, (int)(x * scale), (int)(100 * scale))[1] - Pixel(image, (int)((x + 1) * scale), (int)(100 * scale))[1]));

    internal static RenderTargetBitmap Render(FrameworkElement element, double scale = 1)
    {
        var size = new Size(320, 220);
        element.Measure(size); element.Arrange(new Rect(size)); element.UpdateLayout();
        var image = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(element);
        return image;
    }

    private static byte[] Pixel(BitmapSource source, int x, int y)
    {
        var bytes = new byte[4]; source.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0); return bytes;
    }

    internal static void Export(string name, BitmapSource image)
    {
        if (Environment.GetEnvironmentVariable("MINIC_RENDER_OUTPUT") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
