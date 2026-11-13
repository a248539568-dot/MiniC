using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiniC.Services;

/// <summary>可选的屏幕捕获排除。关闭时不触碰窗口原有的显示亲和性。</summary>
internal sealed class WindowCaptureExclusion : IDisposable
{
    private const uint WdaExcludeFromCapture = 0x11;
    private readonly nint _handle;
    private readonly uint _previous;
    private bool _disposed;

    internal WindowCaptureExclusion(nint handle)
    {
        _handle = handle;
        if (!GetWindowDisplayAffinity(handle, out _previous)
            || !SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool TryGetAffinity(nint handle, out uint affinity) =>
        GetWindowDisplayAffinity(handle, out affinity);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsWindow(_handle) && !SetWindowDisplayAffinity(_handle, _previous))
            MiniCLogger.Warning(nameof(WindowCaptureExclusion), $"Could not restore capture affinity: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
}
