using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace MiniC.Services;

/// <summary>GPU-only capture of one rear window. Its occluders are never part of the source.</summary>
internal sealed class GpuCapturedWindow : IDisposable
{
    private static Task? _accessRequest;
    private static volatile bool _borderlessAllowed;
    private bool _borderlessApplied;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private ID3D11Texture2D? _texture;
    private SizeInt32 _size;
    internal ID3D11ShaderResourceView? Source { get; private set; }
    internal long Version { get; private set; }
    internal bool Required { get; set; }
    private int _closed;
    private bool _disposed;
    private readonly Action? _frameReady;

    internal GpuCapturedWindow(ID3D11Device device, ID3D11DeviceContext context, nint handle, Action? frameReady = null)
    {
        _device = device; _context = context; _frameReady = frameReady;
        _item = CreateItem(handle);
        _size = _item.Size;
        if (_size.Width <= 0 || _size.Height <= 0) throw new InvalidOperationException("Empty capture window.");
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var raw));
            try { _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(raw); }
            finally { Marshal.Release(raw); }
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += FrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            ApplyBorderlessAccess();
            _item.Closed += ItemClosed;
            _session.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args)
    {
        Interlocked.Exchange(ref _closed, 1);
        _frameReady?.Invoke();
    }
    private void FrameArrived(Direct3D11CaptureFramePool sender, object args) => _frameReady?.Invoke();

    // Invoked on the UI thread only when screenshot-visible glass is requested.
    internal static void RequestBorderlessAccess() => _accessRequest ??= RequestAsync();
    private static async Task RequestAsync()
    {
        try
        {
            _borderlessAllowed = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException)
        { MiniCLogger.Warning(nameof(GpuCapturedWindow), $"Borderless capture access unavailable: {error.Message}"); }
    }

    private void ApplyBorderlessAccess()
    {
        if (!_borderlessAllowed || _borderlessApplied) return;
        _session.IsBorderRequired = false;
        _borderlessApplied = true;
    }

    internal void Capture()
    {
        ApplyBorderlessAccess();
        if (Volatile.Read(ref _closed) != 0) throw new InvalidOperationException("Capture window closed.");
        using var frame = LatestFrame();
        if (frame is null) return;
        var size = frame.ContentSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (size.Width != _size.Width || size.Height != _size.Height)
        {
            // Recreate only after returning the frame; never stretch a stale resize frame.
            frame.Dispose();
            _size = size;
            Source?.Dispose(); Source = null;
            _texture?.Dispose(); _texture = null;
            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            Version++;
            return;
        }
        using var surface = frame.Surface;
        using var texture = OpenTexture(surface);
        var description = texture.Description;
        if (description.Width < size.Width || description.Height < size.Height) return;
        if (_texture is null)
        {
            description.Width = (uint)size.Width; description.Height = (uint)size.Height;
            description.Usage = ResourceUsage.Default; description.BindFlags = BindFlags.ShaderResource;
            description.CPUAccessFlags = CpuAccessFlags.None; description.MiscFlags = ResourceOptionFlags.None;
            _texture = _device.CreateTexture2D(description);
            Source = _device.CreateShaderResourceView(_texture);
        }
        _context.CopySubresourceRegion(_texture, 0, 0, 0, 0, texture, 0,
            new Vortice.Mathematics.Box(0, 0, 0, size.Width, size.Height, 1));
        Version++;
    }

    private Direct3D11CaptureFrame? LatestFrame()
    {
        var first = _pool.TryGetNextFrame();
        if (first is null) return null;
        try
        {
            var next = _pool.TryGetNextFrame();
            if (next is null) return first;
            first.Dispose();
            return next;
        }
        catch { first.Dispose(); throw; }
    }

    internal ID3D11ShaderResourceView? SourceFor(System.Windows.Rect bounds) =>
        _texture is not null && Math.Abs(_size.Width - bounds.Width) <= 1 && Math.Abs(_size.Height - bounds.Height) <= 1
            ? Source : null;

    internal static GraphicsCaptureItem CreateItem(nint handle)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var nameHandle));
        nint factory = 0;
        try
        {
            var factoryId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(nameHandle, in factoryId, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            interop.CreateForWindow(handle, in iid, out var pointer);
            try { return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }
        finally { if (factory != 0) Marshal.Release(factory); _ = WindowsDeleteString(nameHandle); }
    }

    internal static ID3D11Texture2D OpenTexture(IDirect3DSurface surface)
    {
        var unknown = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint native = 0;
        try
        {
            var accessId = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in accessId, out native));
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(native);
            var iid = typeof(ID3D11Texture2D).GUID;
            access.GetInterface(in iid, out var pointer);
            return new ID3D11Texture2D(pointer);
        }
        finally { if (native != 0) Marshal.Release(native); Marshal.Release(unknown); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_item is not null) _item.Closed -= ItemClosed;
        if (_pool is not null) _pool.FrameArrived -= FrameArrived;
        try { _session?.Dispose(); }
        finally
        {
            _pool?.Dispose(); Source?.Dispose(); _texture?.Dispose(); _winrtDevice?.Dispose();
        }
    }

    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint graphicsDevice);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(nint window, in Guid iid, out nint result);
        void CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { void GetInterface(in Guid iid, out nint result); }
}
