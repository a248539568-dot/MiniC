using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniC.Controls;
using MiniC.Services;
using Xunit;

namespace MiniC.Tests;

public sealed class BackdropSourceTests
{
    [Fact]
    public async Task SolidSurfaceAndPreviewStayOpaqueEvenWithTransparentTintAndWallpaperRequested()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var solid = new SolidMaterialSurface { Width = 320, Height = 220, UseWallpaper = true,
                Tint = new SolidColorBrush(Color.FromArgb(0, 90, 130, 170)) };
            var image = FrostedGlassTests.Render(solid);
            Assert.Equal(new byte[] { 170, 130, 90, 255 }, Pixel(image, 160, 110));
            var source = solid.Children.OfType<WallpaperGlassSurface>().Single();
            Assert.False(source.UseWallpaper);
            Assert.Equal(Visibility.Collapsed, source.Visibility);
            var preview = solid.CreatePreview();
            preview.Width = 320; preview.Height = 220;
            Assert.Equal(Pixel(image, 160, 110), Pixel(FrostedGlassTests.Render(preview), 160, 110));
            var group = new MiniC.ViewModels.DeskGroup { Material = "Solid", Opacity = 0,
                Appearance = new MiniC.Models.MaterialPreferences { SeeThroughWindows = true } };
            Assert.Equal(1, group.Opacity);
            Assert.False(group.IsSeeThrough);
            Assert.False(group.UsesWallpaper);
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("Crystal")]
    [InlineData("Frosted")]
    [InlineData("Solid")]
    public async Task LiveModeNeverDrawsWallpaperAndPreservesTintFrameAndPreview(string material)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            GlassMaterialSurface glass = material switch
            {
                "Crystal" => WallpaperRenderHost.Crystal(FrostedGlassTests.Texture()),
                "Frosted" => WallpaperRenderHost.Frosted(FrostedGlassTests.Texture()),
                _ => new SolidMaterialSurface()
            };
            glass.Width = 320; glass.Height = 220; glass.Radius = 24;
            glass.Tint = GroupMaterialBrushes.Background("Blue", material, 0.62);
            FrostedGlassTests.Render(glass);
            using var host = await WallpaperRenderHost.Load(glass, glass);
            if (material != "Solid") Assert.Equal(255, Pixel(FrostedGlassTests.Render(glass), 160, 110)[3]);
            var mask = glass.OpacityMask;
            var tint = glass.Tint;
            glass.UseWallpaper = false;
            var image = FrostedGlassTests.Render(glass);
            Assert.Equal(((SolidColorBrush)tint).Color.A, Pixel(image, 160, 110)[3]);
            Assert.Equal(0, Pixel(image, 0, 0)[3]);
            Assert.Same(mask, glass.OpacityMask);
            Assert.Same(tint, glass.Tint);
            Assert.Null(glass.Children.OfType<WallpaperGlassSurface>().Single().Effect);
            var preview = glass.CreatePreview();
            Assert.False(preview.UseWallpaper);
            Assert.Same(tint, preview.Tint);
            Assert.Equal(24, preview.Radius);
        });
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(false, 24)]
    public async Task WallpaperIsOnlyLoadedForDesktopAndCannotLeakIntoLiveMode(bool refraction, double blur)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var calls = 0;
            var scene = new WallpaperScene([new WallpaperMonitor(new Rect(-10000, -10000, 20000, 20000), null)], Colors.Lime, 4);
            var surface = new WallpaperGlassSurface(() => { calls++; return Task.FromResult(scene); })
            { Width = 320, Height = 220, RefractionEnabled = refraction, BlurRadius = blur };
            using var host = new HwndSource(new HwndSourceParameters("MiniC hidden source test")
            { WindowStyle = unchecked((int)0x80000000), Width = 320, Height = 220 });
            try
            {
                host.RootVisual = surface;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await surface.RefreshAsync();
                Assert.Equal(0, calls);
                Assert.Equal(new byte[4], Pixel(FrostedGlassTests.Render(surface), 160, 110));
                Assert.Null(surface.Effect);
                surface.UseWallpaper = true;
                await surface.RefreshAsync();
                Assert.True(calls > 0);
                Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(FrostedGlassTests.Render(surface), 160, 110));
                surface.UseWallpaper = false;
                var before = calls;
                await surface.RefreshAsync();
                Assert.Equal(before, calls);
                Assert.Null(surface.Effect);
                Assert.Equal(new byte[4], Pixel(FrostedGlassTests.Render(surface), 160, 110));
                Assert.False(IsWindowVisible(host.Handle));
            }
            finally { host.RootVisual = null; }
        });
    }

    [Fact]
    public async Task PendingWallpaperLoadCannotReturnAfterSwitchingToLiveMode()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var pending = new TaskCompletionSource<WallpaperScene>();
            var surface = new WallpaperGlassSurface(() => pending.Task) { Width = 320, Height = 220, UseWallpaper = true };
            using var host = new HwndSource(new HwndSourceParameters("MiniC hidden pending source test")
            { WindowStyle = unchecked((int)0x80000000), Width = 320, Height = 220 });
            try
            {
                host.RootVisual = surface;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var loading = surface.RefreshAsync();
                surface.UseWallpaper = false;
                pending.SetResult(new WallpaperScene([new WallpaperMonitor(new Rect(-10000, -10000, 20000, 20000), null)], Colors.Black, 4));
                await loading;
                Assert.Equal(new byte[4], Pixel(FrostedGlassTests.Render(surface), 160, 110));
                Assert.Null(surface.Effect);
                Assert.False(IsWindowVisible(host.Handle));
            }
            finally { host.RootVisual = null; }
        });
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var bytes = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0); return bytes;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
}
