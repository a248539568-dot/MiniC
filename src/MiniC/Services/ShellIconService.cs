using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiniC.Services;

/// <summary>
/// 从 Windows Shell 提取标准图标与缩略图，并使用有容量上限的缓存降低重复解码开销。
/// </summary>
public static class ShellIconService
{
    private sealed record CachedShellImage(long LastWriteTicks, ImageSource Image);

    private const int CacheCapacity = 512;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (CachedShellImage Image, LinkedListNode<string> Node)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> CacheOrder = new();
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint SiigbfBiggerSizeOk = 0x00000001;
    private const uint SiigbfIconOnly = 0x00000004;
    private const uint SiigbfScaleUp = 0x00000100;
    private const uint ShgsiIcon = 0x000000100;
    private const uint ShgsiLargeIcon = 0x000000000;
    private const uint SiidRecycler = 31;
    private const uint SiidRecyclerFull = 32;
    private static readonly Guid ShellItemImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static ImageSource? GetIcon(string path, bool isDirectory, bool useCache = true)
    {
        long lastWriteTicks;
        try { lastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { lastWriteTicks = 0; }

        if (useCache)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(path, out var cached) && cached.Image.LastWriteTicks == lastWriteTicks)
                {
                    CacheOrder.Remove(cached.Node);
                    CacheOrder.AddFirst(cached.Node);
                    return cached.Image.Image;
                }
            }
        }

        var image = GetNativeShellImage(path, iconOnly: false)
                    ?? GetNativeShellImage(path, iconOnly: true)
                    ?? GetLegacyIcon(path, isDirectory);
        if (image is not null)
        {
            lock (CacheLock)
            {
                if (Cache.Remove(path, out var existing)) CacheOrder.Remove(existing.Node);
                var node = CacheOrder.AddFirst(path);
                Cache[path] = (new CachedShellImage(lastWriteTicks, image), node);
                while (Cache.Count > CacheCapacity && CacheOrder.Last is { } oldest)
                {
                    CacheOrder.RemoveLast();
                    Cache.Remove(oldest.Value);
                }
            }
        }
        return image;
    }

    /// <summary>按已确认的空/满状态读取 Windows 回收站图标，避免 Shell 虚拟项目缓存滞后。</summary>
    public static ImageSource? GetRecycleBinIcon(bool isEmpty)
    {
        var info = new ShStockIconInfo { Size = (uint)Marshal.SizeOf<ShStockIconInfo>() };
        var result = SHGetStockIconInfo(
            isEmpty ? SiidRecycler : SiidRecyclerFull,
            ShgsiIcon | ShgsiLargeIcon,
            ref info);
        if (result < 0 || info.IconHandle == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static ImageSource? GetNativeShellImage(string path, bool iconOnly)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            var interfaceId = ShellItemImageFactoryId;
            var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out factory);
            if (result < 0 || factory is null) return null;

            var flags = SiigbfBiggerSizeOk | SiigbfScaleUp;
            if (iconOnly) flags |= SiigbfIconOnly;
            result = factory.GetImage(new NativeSize(64, 64), flags, out var bitmapHandle);
            if (result < 0 || bitmapHandle == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmapHandle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(bitmapHandle);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory is not null && Marshal.IsComObject(factory))
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }
    }

    private static ImageSource? GetLegacyIcon(string path, bool isDirectory)
    {
        var flags = ShgfiIcon | ShgfiLargeIcon;
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        if (!File.Exists(path) && !Directory.Exists(path)) flags |= ShgfiUseFileAttributes;

        var result = SHGetFileInfo(path, attributes, out var info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public readonly int Width;
        public readonly int Height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShStockIconInfo
    {
        public uint Size;
        public IntPtr IconHandle;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetStockIconInfo(uint stockIconId, uint flags, ref ShStockIconInfo stockIconInfo);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
