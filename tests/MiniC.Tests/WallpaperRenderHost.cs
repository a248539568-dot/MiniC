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

/// <summary>Injects wallpaper through the production loader, without a visible window.</summary>
internal sealed class WallpaperRenderHost : IDisposable
{
    private readonly HwndSource _host;
    private bool _disposed;

    private WallpaperRenderHost(FrameworkElement surface)
    {
        _host = new HwndSource(new HwndSourceParameters("MiniC hidden wallpaper render")
        {
            WindowStyle = unchecked((int)0x80000000),
            Width = Math.Max(1, (int)Math.Ceiling(surface.ActualWidth)),
            Height = Math.Max(1, (int)Math.Ceiling(surface.ActualHeight))
        });
        _host.RootVisual = surface;
        Assert.False(IsWindowVisible(_host.Handle));
    }

    internal static CrystalGlassSurface Crystal(BitmapSource image)
    {
        CrystalGlassSurface glass = null!;
        glass = new CrystalGlassSurface(() => Task.FromResult(Scene(glass, image))) { UseWallpaper = true };
        return glass;
    }

    internal static FrostedGlassSurface Frosted(BitmapSource image)
    {
        FrostedGlassSurface glass = null!;
        glass = new FrostedGlassSurface(() => Task.FromResult(Scene(glass, image))) { UseWallpaper = true };
        return glass;
    }

    private static WallpaperScene Scene(GlassMaterialSurface glass, BitmapSource image)
    {
        var source = glass.Children.OfType<WallpaperGlassSurface>().Single();
        var origin = source.PointToScreen(new System.Windows.Point());
        var end = source.PointToScreen(new System.Windows.Point(source.ActualWidth, source.ActualHeight));
        return new WallpaperScene([new WallpaperMonitor(new Rect(origin, end), image)], Colors.Gray, 2);
    }

    internal static async Task<WallpaperRenderHost> Load(FrameworkElement root, GlassMaterialSurface glass)
    {
        var host = new WallpaperRenderHost(root);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await glass.RefreshAsync();
            return host;
        }
        catch { host.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.RootVisual = null; _host.Dispose();
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
}
