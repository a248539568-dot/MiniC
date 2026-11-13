using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MiniC.Services;

/// <summary>One outstanding frame per surface, paced by DXGI rather than a fixed-rate timer.</summary>
internal sealed class GpuGlassSwapChain : IDisposable
{
    internal readonly IDXGISwapChain1 Native;
    private readonly SafeWaitHandle _latency;
    private readonly object _deviceGate;
    private int _ready;
    internal bool WaitingForFrame { get; private set; }
    internal nint LatencyHandle => _latency.DangerousGetHandle();

    internal GpuGlassSwapChain(ID3D11Device device, int width, int height, object? deviceGate = null)
    {
        _deviceGate = deviceGate ?? new object();
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        Native = factory.CreateSwapChainForComposition(device, new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.FrameLatencyWaitableObject
        });
        try
        {
            using var chain = Native.QueryInterface<IDXGISwapChain2>();
            chain.MaximumFrameLatency = 1;
            _latency = new SafeWaitHandle(chain.FrameLatencyWaitableObject, ownsHandle: true);
            if (_latency.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    internal bool TryBeginFrame()
    {
        if (Volatile.Read(ref _ready) != 0) { WaitingForFrame = false; return true; }
        var result = WaitForSingleObject(_latency, 0);
        if (result == 0x102) { WaitingForFrame = true; return false; }
        if (result != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        AcceptReadySignal();
        return true;
    }

    internal void AcceptReadySignal()
    {
        WaitingForFrame = false;
        Volatile.Write(ref _ready, 1);
    }

    internal void Resize(int width, int height) => Native.ResizeBuffers(2, (uint)width, (uint)height,
        Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();

    internal void Present(nint pinnedPointer)
    {
        // The dispatch owns another COM reference, so output/device resets cannot invalidate it.
        Marshal.AddRef(pinnedPointer);
        using var chain = new IDXGISwapChain1(pinnedPointer);
        lock (_deviceGate)
        {
            chain.Present(0, PresentFlags.None).CheckError();
            Volatile.Write(ref _ready, 0);
        }
    }

    public void Dispose() { _latency?.Dispose(); Native?.Dispose(); }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
