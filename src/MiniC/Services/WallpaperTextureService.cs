using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using FormsScreen = System.Windows.Forms.Screen;

namespace MiniC.Services;

internal sealed record WallpaperMonitor(Rect Bounds, BitmapSource? Image);
internal sealed record WallpaperScene(IReadOnlyList<WallpaperMonitor> Monitors, Color Background, int Position);

/// <summary>只读取桌面壁纸，不捕获其他窗口或桌面文件；图像缓存和解码在后台完成。</summary>
internal static class WallpaperTextureService
{
    private static readonly object CacheLock = new();
    private static Task<WallpaperScene>? _cached;
    private static long _version;
    internal static long Version { get { lock (CacheLock) return _version; } }
    public static Task<WallpaperScene> GetAsync() => GetOrLoad(Load);

    internal static Task<WallpaperScene> GetOrLoad(Func<WallpaperScene> load)
    {
        lock (CacheLock)
        {
            if (_cached is { } cached) return cached;
            var pending = Task.Run(load);
            _cached = pending;
            _ = pending.ContinueWith(completed =>
            {
                if (!completed.IsFaulted && !completed.IsCanceled) return;
                lock (CacheLock)
                    if (ReferenceEquals(_cached, pending)) _cached = null;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return pending;
        }
    }

    public static void Invalidate()
    {
        lock (CacheLock)
        {
            _cached = null;
            _version++;
        }
    }

    private static WallpaperScene Load()
    {
        IDesktopWallpaper? wallpaper = null;
        try
        {
            wallpaper = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"), true)!)!;
            wallpaper.GetMonitorDevicePathCount(out var count);
            wallpaper.GetBackgroundColor(out var color);
            wallpaper.GetPosition(out var position);
            var cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var monitors = new List<WallpaperMonitor>();
            var fallbackScreens = FormsScreen.AllScreens;
            for (uint index = 0; index < count; index++)
            {
                wallpaper.GetMonitorDevicePathAt(index, out var id);
                Rect bounds;
                try
                {
                    wallpaper.GetMonitorRECT(id, out var rect);
                    bounds = ToRect(rect);
                }
                catch (COMException error) when (index < fallbackScreens.Length)
                {
                    // GetMonitorRECT can temporarily fail while Explorer/DWM
                    // rebuilds the monitor topology. The WinForms screen list
                    // still exposes the same virtual-screen coordinates, so a
                    // transient COM failure must not turn the glass invisible.
                    var screen = fallbackScreens[index].Bounds;
                    bounds = new Rect(screen.Left, screen.Top,
                        Math.Max(1, screen.Width), Math.Max(1, screen.Height));
                    MiniCLogger.Warning(nameof(WallpaperTextureService),
                        $"Wallpaper monitor bounds unavailable from DWM; using screen fallback for monitor {index}: {error.Message}");
                }
                wallpaper.GetWallpaper(id, out var path);
                BitmapSource? image = null;
                if (!string.IsNullOrEmpty(path))
                {
                    if (!cache.TryGetValue(path, out image))
                    {
                        try
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            var bitmap = new BitmapImage(); bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = stream;
                            bitmap.EndInit(); bitmap.Freeze(); image = bitmap; cache[path] = bitmap;
                        }
                        catch (Exception error) when (error is IOException or NotSupportedException or System.IO.FileFormatException)
                        {
                            MiniCLogger.Warning(nameof(WallpaperTextureService), $"Wallpaper image could not be decoded: {error.Message}");
                            throw;
                        }
                    }
                }
                monitors.Add(new WallpaperMonitor(bounds, image));
            }
            return new WallpaperScene(monitors, Color.FromRgb((byte)(color & 255), (byte)((color >> 8) & 255), (byte)((color >> 16) & 255)), position);
        }
        catch (Exception error)
        {
            MiniCLogger.Error(nameof(WallpaperTextureService), error, "Wallpaper refraction unavailable; keeping the previous scene.");
            throw;
        }
        finally { if (wallpaper is not null) Marshal.ReleaseComObject(wallpaper); }
    }

    internal static Rect FitImage(Rect screen, double width, double height, int position)
    {
        if (position == 2) return screen;
        var factor = position == 0 ? 1 : position == 3 ? Math.Min(screen.Width / width, screen.Height / height)
            : Math.Max(screen.Width / width, screen.Height / height);
        var w = width * factor; var h = height * factor;
        return new Rect(screen.Left + (screen.Width-w)/2, screen.Top + (screen.Height-h)/2, w, h);
    }

    private static Rect ToRect(NativeRect rect) =>
        new(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] out string path);
        void GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetMonitorDevicePathCount(out uint count);
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string id, out NativeRect rect);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(int position);
        void GetPosition(out int position);
    }
}
