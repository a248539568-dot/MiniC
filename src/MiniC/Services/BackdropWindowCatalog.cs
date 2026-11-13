using System.Runtime.InteropServices;
using System.Windows;

namespace MiniC.Services;

internal sealed record BackdropWindow(nint Handle, Rect Bounds, uint ProcessId,
    byte Opacity = 255, uint? ColorKey = null)
{
    internal Rect OpaqueInterior { get; init; } = Rect.Empty;
}

/// <summary>Z-order snapshot. Capture individual rear windows, never the glass output itself.</summary>
internal static class BackdropWindowCatalog
{
    internal static BackdropWindow[] Snapshot()
    {
        var windows = new List<BackdropWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            if (GetWindowThreadProcessId(handle, out var process) == 0) return true;
            if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (process == Environment.ProcessId)
            {
                windows.Add(new(handle, Rect.Empty, process));
                return true;
            }
            if (DwmGetWindowAttribute(handle, 9, out NativeRect native, Marshal.SizeOf<NativeRect>()) != 0
                && !GetWindowRect(handle, out native)) return true;
            if (native.Right > native.Left && native.Bottom > native.Top)
            {
                var appearance = ReadLayeredAppearance(handle);
                windows.Add(new(handle, new Rect(native.Left, native.Top, native.Right-native.Left, native.Bottom-native.Top),
                    process, appearance.Opacity, appearance.ColorKey)
                { OpaqueInterior = ReadOpaqueInterior(handle) });
            }
            return true;
        }, 0);
        return windows.ToArray();
    }

    internal static BackdropWindow[] Behind(IEnumerable<BackdropWindow> windows, nint foreground, Rect bounds, uint ownProcess)
    {
        var behind = foreground == 0;
        var result = new List<BackdropWindow>();
        foreach (var window in windows)
        {
            if (window.Handle == foreground) { behind = true; continue; }
            if (behind && window.ProcessId != ownProcess && window.Opacity != 0 && window.Bounds.IntersectsWith(bounds))
            {
                result.Add(window);
                if (Covers(window, bounds)) break;
            }
        }
        result.Reverse(); // Back to front; preserve transparent portions of nearer windows.
        return result.ToArray();
    }

    internal static bool IsExposed(IEnumerable<BackdropWindow> windows, nint foreground, Rect bounds, uint ownProcess)
    {
        if (foreground == 0) return true; // Offscreen surfaces do not have a desktop Z-order.
        var covered = false;
        foreach (var window in windows)
        {
            if (window.Handle == foreground) return !covered;
            if (window.ProcessId != ownProcess && Covers(window, bounds)) covered = true;
        }
        return false; // Hidden, minimized, cloaked or closed host.
    }

    private static bool Covers(BackdropWindow window, Rect bounds) =>
        window.Opacity == 255 && window.ColorKey is null && !bounds.IsEmpty &&
        !window.OpaqueInterior.IsEmpty && window.OpaqueInterior.Contains(bounds);

    internal static Rect ReadOpaqueInterior(nint handle)
    {
        // Be conservative: layered/DirectComposition, transparent and shaped windows may have holes.
        var styles = GetWindowLongPtr(handle, -20).ToInt64();
        if ((styles & (0x80000 | 0x20 | 0x200000)) != 0 ||
            (GetWindowLongPtr(handle, -16).ToInt64() & 0xC00000) != 0xC00000 ||
            DwmGetWindowAttribute(handle, 38, out int backdrop, sizeof(int)) == 0 && backdrop > 1)
            return Rect.Empty;
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return Rect.Empty;
        try { if (GetWindowRgn(handle, region) != 0) return Rect.Empty; }
        finally { _ = DeleteObject(region); }
        if (!GetClientRect(handle, out var client)) return Rect.Empty;
        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin)) return Rect.Empty;
        var inset = Math.Ceiling(32 * Math.Max(96u, GetDpiForWindow(handle)) / 96d);
        var width = client.Right - client.Left - 2 * inset;
        var height = client.Bottom - client.Top - 2 * inset;
        return width > 0 && height > 0 ? new Rect(origin.X + inset, origin.Y + inset, width, height) : Rect.Empty;
    }

    internal static (byte Opacity, uint? ColorKey) ReadLayeredAppearance(nint handle)
    {
        // WGC pixels do not include SetLayeredWindowAttributes' global opacity/color key.
        // UpdateLayeredWindow has per-pixel alpha instead; GetLayeredWindowAttributes fails for it.
        if ((GetWindowLongPtr(handle, -20).ToInt64() & 0x80000) != 0
            && GetLayeredWindowAttributes(handle, out var key, out var alpha, out var flags))
            return ((flags & 2) != 0 ? alpha : (byte)255, (flags & 1) != 0 ? key & 0xFFFFFFu : null);
        return (255, null);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    private delegate bool EnumWindowProc(nint handle, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint handle, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint handle, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
