using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace MiniC.Services;

internal sealed record GlassWindowMove(int X, int Y, long Session);

/// <summary>Physical coordinates; paired windows move in one Win32 batch without changing activation.</summary>
internal static class NativeWindowPlacement
{
    internal static IReadOnlyList<nint> TopToBottom(IEnumerable<nint> handles)
    {
        var remaining = handles.Where(handle => handle != 0).ToHashSet();
        var ordered = new List<nint>(remaining.Count);
        if (remaining.Count == 0) return ordered;
        var visited = new HashSet<nint>();
        // Bound traversal if another process changes or destroys windows during the snapshot.
        for (var handle = GetTopWindow(0); handle != 0 && visited.Add(handle); handle = GetWindow(handle, 2))
        {
            if (remaining.Remove(handle)) ordered.Add(handle);
            if (remaining.Count == 0) break;
        }
        return ordered;
    }

    internal static bool TryGetBounds(nint handle, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!GetWindowRect(handle, out var native)) return false;
        bounds = new Rect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        return true;
    }

    internal static void Move(nint handle, int x, int y)
    {
        if (!SetWindowPos(handle, 0, x, y, 0, 0, 0x0010 | 0x0004 | 0x0001))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void MovePair(nint foreground, nint background, GlassWindowMove move, Rect backgroundBounds)
    {
        var batch = BeginDeferWindowPos(2);
        if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        batch = DeferWindowPos(batch, foreground, 0, move.X, move.Y, 0, 0, 0x0010 | 0x0004 | 0x0001);
        if (batch == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        batch = DeferWindowPos(batch, background, 0, (int)backgroundBounds.X, (int)backgroundBounds.Y,
            (int)backgroundBounds.Width, (int)backgroundBounds.Height, 0x0010 | 0x0004);
        if (batch == 0 || !EndDeferWindowPos(batch)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint DeferWindowPos(nint batch, nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndDeferWindowPos(nint batch);
}
