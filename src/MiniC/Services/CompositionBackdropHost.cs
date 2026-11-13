using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.UI.Composition;

namespace MiniC.Services;

/// <summary>Non-interactive native surface for system and GPU material backgrounds.</summary>
internal sealed class CompositionBackdropHost : IDisposable
{
    private static nint _dispatcherController;
    private readonly Window _foreground;
    private readonly FrameworkElement _surface;
    private readonly Windows.UI.Composition.Desktop.DesktopWindowTarget _target;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private (int Width, int Height, int Radius)? _region;
    private Rect _placedBounds = Rect.Empty;
    private bool _disposed;
    private CompositionSurfaceBrush? _swapChainBrush;
    private ICompositionSurface? _swapChainSurface;
    private bool _visible;

    public CompositionBackdropHost(Window foreground, FrameworkElement surface)
    {
        _foreground = foreground;
        _surface = surface;
        if (_dispatcherController == 0)
        {
            var options = new DispatcherOptions { Size = Marshal.SizeOf<DispatcherOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _dispatcherController));
        }
        Handle = CreateWindowEx(0x00200000 | 0x80 | 0x08000000 | 0x20, "static", "MiniC material surface",
            unchecked((int)0x80000000), 0, 0, 1, 1, 0, 0, 0, 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var enabled = 1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, 17, ref enabled, sizeof(int)));
            Compositor = new Compositor();
            var unknown = Marshal.GetIUnknownForObject(Compositor);
            nint native = 0;
            try
            {
                var id = typeof(ICompositorDesktopInterop).GUID;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in id, out native));
                var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(native);
                interop.CreateDesktopWindowTarget(Handle, true, out var raw);
                try { _target = WinRT.MarshalInterface<Windows.UI.Composition.Desktop.DesktopWindowTarget>.FromAbi(raw); }
                finally { Marshal.Release(raw); }
            }
            finally
            {
                if (native != 0) Marshal.Release(native);
                Marshal.Release(unknown);
            }
            Root = Compositor.CreateSpriteVisual();
            Root.Brush = Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _geometry = Compositor.CreateRoundedRectangleGeometry();
            Root.Clip = Compositor.CreateGeometricClip(_geometry);
            _target.Root = Root;
        }
        catch { Dispose(); throw; }
    }

    public nint Handle { get; }
    public Compositor Compositor { get; } = null!;
    public SpriteVisual Root { get; } = null!;
    public double Scale { get; private set; } = 1;
    public Rect Bounds { get; private set; }

    public void AttachSwapChain(nint swapChain)
    {
        if (_disposed) return;
        var unknown = Marshal.GetIUnknownForObject(Compositor);
        nint native = 0;
        try
        {
            var id = typeof(ICompositorInterop).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in id, out native));
            var interop = (ICompositorInterop)Marshal.GetObjectForIUnknown(native);
            interop.CreateCompositionSurfaceForSwapChain(swapChain, out var raw);
            ICompositionSurface surface;
            try { surface = WinRT.MarshalInterface<ICompositionSurface>.FromAbi(raw); }
            finally { Marshal.Release(raw); }
            var brush = Compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Fill;
            Root.Brush = brush;
            _swapChainBrush?.Dispose();
            (_swapChainSurface as IDisposable)?.Dispose();
            _swapChainBrush = brush;
            _swapChainSurface = surface;
        }
        finally
        {
            if (native != 0) Marshal.Release(native);
            Marshal.Release(unknown);
        }
    }

    public void DetachSwapChain()
    {
        if (_disposed) return;
        Root.Brush = Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _swapChainBrush?.Dispose();
        (_swapChainSurface as IDisposable)?.Dispose();
        _swapChainBrush = null;
        _swapChainSurface = null;
    }

    internal void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        if (SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | (visible ? 0x40u : 0x80u)))
            _visible = visible;
    }

    public bool Align(double radius, bool show = true)
    {
        if (_disposed || !_foreground.IsLoaded || !_foreground.IsVisible || !_surface.IsVisible) return false;
        var origin = _surface.PointToScreen(new System.Windows.Point());
        var end = _surface.PointToScreen(new System.Windows.Point(_surface.ActualWidth, _surface.ActualHeight));
        var width = Math.Max(1, (int)Math.Round(end.X - origin.X));
        var height = Math.Max(1, (int)Math.Round(end.Y - origin.Y));
        var scale = width / Math.Max(1, _surface.ActualWidth);
        Scale = scale;
        Bounds = new Rect(Math.Round(origin.X), Math.Round(origin.Y), width, height);
        var physicalRadius = (int)Math.Round(Math.Min(Math.Max(0, radius) * scale, Math.Min(width, height) / 2d));
        Root.Size = new Vector2(width, height);
        _geometry.Size = Root.Size;
        _geometry.CornerRadius = new Vector2(physicalRadius);
        var region = (width, height, physicalRadius);
        if (_region != region)
        {
            var outer = CreateRoundRectRgn(0, 0, width + 1, height + 1, physicalRadius * 2, physicalRadius * 2);
            if (outer == 0) return false;
            try
            {
                if (SetWindowRgn(Handle, outer, false) == 0) return false;
                outer = 0; // SetWindowRgn transfers ownership to Windows.
                _region = region;
            }
            finally { if (outer != 0) _ = DeleteObject(outer); }
        }
        var foregroundHandle = new WindowInteropHelper(_foreground).Handle;
        if (_placedBounds == Bounds && GetWindow(Handle, 3) == foregroundHandle)
        { SetVisible(show); return true; }
        if (!SetWindowPos(Handle, foregroundHandle, (int)Bounds.X, (int)Bounds.Y, width, height, 0x10)) return false;
        _placedBounds = Bounds;
        SetVisible(show);
        return true;
    }

    internal void CommitMove(GpuGlassPose pose)
    {
        if (_disposed || pose.Move is null) return;
        var bounds = new Rect(pose.X, pose.Y, pose.Width, pose.Height);
        var foregroundHandle = new WindowInteropHelper(_foreground).Handle;
        NativeWindowPlacement.MovePair(foregroundHandle, Handle, pose.Move, bounds);
        Bounds = _placedBounds = bounds;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_target is not null) { _target.Root = null; (_target as IDisposable)?.Dispose(); }
        }
        catch (Exception error) { MiniCLogger.Warning(nameof(CompositionBackdropHost), $"Material surface cleanup failed: {error.Message}"); }
        finally
        {
            try
            {
                _swapChainBrush?.Dispose();
                (_swapChainSurface as IDisposable)?.Dispose();
                Compositor?.Dispose();
            }
            catch (Exception error) { MiniCLogger.Warning(nameof(CompositionBackdropHost), $"Compositor cleanup failed: {error.Message}"); }
            finally { _ = DestroyWindow(Handle); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DispatcherOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherOptions options, out nint controller);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int extendedStyle, string className, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint region);
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop { void CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target); }
    [ComImport, Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorInterop
    {
        void CreateCompositionSurfaceForHandle(nint handle, out nint surface);
        void CreateCompositionSurfaceForSwapChain(nint swapChain, out nint surface);
        void CreateGraphicsDevice(nint device, out nint graphicsDevice);
    }
}
