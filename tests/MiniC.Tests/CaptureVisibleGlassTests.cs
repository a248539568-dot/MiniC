using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using MiniC.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;
using Xunit;
using Rect = System.Windows.Rect;

namespace MiniC.Tests;

[CollectionDefinition("Shared desktop renderer", DisableParallelization = true)]
public sealed class SharedDesktopRendererCollection;

[Collection("Shared desktop renderer")]
public sealed class CaptureVisibleGlassTests
{
    [Fact]
    public async Task LayeredWindowAttributesAreReadWithoutShowingOrActivatingTheWindow()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var window = new Window { Width = 100, Height = 90, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                Assert.True(SetLayeredWindowAttributes(handle, 0, 0, 2));
                Assert.Equal(((byte)0, (uint?)null), BackdropWindowCatalog.ReadLayeredAppearance(handle));
                Assert.True(SetLayeredWindowAttributes(handle, 0x123456, 128, 3));
                Assert.Equal(((byte)128, (uint?)0x123456), BackdropWindowCatalog.ReadLayeredAppearance(handle));
                Assert.True(SetLayeredWindowAttributes(handle, 0x654321, 0, 1));
                Assert.Equal(((byte)255, (uint?)0x654321), BackdropWindowCatalog.ReadLayeredAppearance(handle));
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void ActivationAndSwitchingBoxesNeverSelectAZeroOpacityOverlay()
    {
        var bounds = new Rect(0, 0, 80, 64);
        var first = new BackdropWindow(1, Rect.Empty, 7);
        var second = new BackdropWindow(2, Rect.Empty, 7);
        var invisible = new BackdropWindow(3, bounds, 8, 0);
        var desktop = new BackdropWindow(4, bounds, 9);
        var partial = new BackdropWindow(5, bounds, 10, 128);
        Assert.Equal(new[] { desktop }, BackdropWindowCatalog.Behind([invisible, first, second, desktop], 1, bounds, 7));
        Assert.Equal(new[] { desktop, partial }, BackdropWindowCatalog.Behind([first, invisible, partial, second, desktop], 1, bounds, 7));
        Assert.Equal(new[] { desktop, partial }, BackdropWindowCatalog.Behind([second, invisible, partial, first, desktop], 2, bounds, 7));
        using var gpu = new TestGpu();
        using var source = new GpuWindowBackdropSource(gpu.Device, gpu.Context);
        Assert.NotEqual(source.Version([partial]), source.Version([partial with { Opacity = 255 }]));
        Assert.NotEqual(source.Version([partial]), source.Version([partial with { ColorKey = 0 }]));
    }

    [Fact]
    public void SharedCompositorAppliesGlobalOpacityAndColorKeyForEverySurface()
    {
        using var gpu = new TestGpu();
        using var compositor = new GpuBackdropCompositor(gpu.Device, gpu.Context);
        using var blue = gpu.Texture(1, 1, 0xFF0000FF);
        using var red = gpu.Texture(1, 1, 0xFFFF0000);
        using var black = gpu.Texture(1, 1, 0xFF000000); // Invisible overlay's actual WGC pixels.
        using var halfRed = gpu.Texture(1, 1, 0x80800000);
        using var keyed = gpu.Texture(2, 1);
        uint[] keyedPixels = [0xFF000000, 0xFFFF0000];
        gpu.Context.UpdateSubresource(keyedPixels, keyed, rowPitch: 8);
        using var blueSource = gpu.Device.CreateShaderResourceView(blue);
        using var redSource = gpu.Device.CreateShaderResourceView(red);
        using var blackSource = gpu.Device.CreateShaderResourceView(black);
        using var halfRedSource = gpu.Device.CreateShaderResourceView(halfRed);
        using var keyedSource = gpu.Device.CreateShaderResourceView(keyed);
        var bounds = new Rect(0, 0, 80, 64);
        for (var surface = 0; surface < 2; surface++)
        {
            using var scene = gpu.Texture(80, 64);
            using var target = gpu.Device.CreateRenderTargetView(scene);
            compositor.Draw(target, bounds, blueSource, bounds);
            compositor.Draw(target, bounds, blackSource, bounds, opacity: 0);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(gpu.Read(scene, 80, 64), 80, 40, 32));
            compositor.Draw(target, bounds, blackSource, bounds, colorKey: 0);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(gpu.Read(scene, 80, 64), 80, 40, 32));
            compositor.Draw(target, bounds, redSource, bounds, opacity: 128 / 255f);
            Assert.Equal(new byte[] { 127, 0, 128, 255 }, Pixel(gpu.Read(scene, 80, 64), 80, 40, 32));
            compositor.Draw(target, bounds, redSource, bounds, colorKey: 0x0000FF); // COLORREF stores red first.
            Assert.Equal(new byte[] { 127, 0, 128, 255 }, Pixel(gpu.Read(scene, 80, 64), 80, 40, 32));
            compositor.Draw(target, bounds, blueSource, bounds);
            compositor.Draw(target, bounds, halfRedSource, bounds, opacity: 128 / 255f);
            Assert.Equal(new byte[] { 191, 0, 64, 255 }, Pixel(gpu.Read(scene, 80, 64), 80, 40, 32));
            compositor.Draw(target, bounds, blueSource, bounds);
            compositor.Draw(target, bounds, keyedSource, bounds, colorKey: 0);
            var mixed = gpu.Read(scene, 80, 64);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(mixed, 80, 0, 32));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(mixed, 80, 79, 32));
            var edge = Pixel(mixed, 80, 40, 32);
            Assert.InRange(edge[0] + edge[2], 254, 256); // No black fringe at the color-key boundary.
        }
    }

    [Fact]
    public async Task CapturePolicyAcknowledgesOnlyASafeFrameAndDiscardsSupersededRequests()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var client = GpuGlassRenderService.Shared.Register(
                System.Windows.Threading.Dispatcher.CurrentDispatcher, (_, _) => { });
            var calls = 0;
            client.SetCapturePolicy(false, () => calls++);
            Assert.Null(client.CaptureCommit(false)); // An old DXGI iteration cannot expose the box.
            var stale = Assert.IsType<Action>(client.CaptureCommit(true));
            client.SetCapturePolicy(true, () => calls++);
            stale();
            Assert.Equal(0, calls);
            var ready = Assert.IsType<Action>(client.CaptureCommit(false));
            ready(); ready();
            Assert.Equal(1, calls);
            Assert.Null(client.CaptureCommit(false));
            client.SetCapturePolicy(false, () => calls++);
            var disposed = Assert.IsType<Action>(client.CaptureCommit(true));
            client.Dispose(); disposed();
            Assert.Equal(1, calls);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task VisibleWorkerWaitsForCapturePolicyAndKeepsItsSwapChainAcrossToggles()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var service = GpuGlassRenderService.Shared;
            IDXGISwapChain1 chain = null;
            var attaches = 0;
            using var client = service.Register(System.Windows.Threading.Dispatcher.CurrentDispatcher, (pointer, available) =>
            {
                Assert.True(available);
                attaches++;
                Assert.Null(chain);
                Marshal.AddRef(pointer);
                chain = new IDXGISwapChain1(pointer);
            }, foreground: GetShellWindow(), hideFromCapture: false);
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                Task WaitForPolicy(bool hidden)
                {
                    var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    client.SetCapturePolicy(hidden, () =>
                    {
                        try
                        {
                            Assert.NotNull(chain);
                            Assert.Equal(hidden, client.HideFromCapture);
                            Assert.True(client.CommitPending);
                            complete.SetResult();
                        }
                        catch (Exception error) { complete.SetException(error); }
                    });
                    return complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                var first = WaitForPolicy(false);
                client.Update(new(bounds.Left + 40, bounds.Top + 40, 100, 90, 1, 8, 1, 0, 0.35f, 1));
                await first;
                for (var index = 0; index < 3; index++)
                {
                    await WaitForPolicy(true);
                    await WaitForPolicy(false);
                }
                Assert.Equal(1, attaches);
            }
            finally { client.Dispose(); chain?.Dispose(); }
        });
    }

    [Fact]
    public async Task ActualShellCaptureWaitsForPixelsBeforeComposingAndRefracting()
    {
        await Task.Run(() =>
        {
            Marshal.ThrowExceptionForHR(RoInitialize(1));
            try
            {
                using var gpu = new TestGpu();
                using var source = new GpuWindowBackdropSource(gpu.Device, gpu.Context);
                var window = Assert.Single(BackdropWindowCatalog.Snapshot(), w => w.Handle == GetShellWindow());
                Assert.False(source.IsReady([window]));
                var deadline = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    source.Prepare([window]);
                    Assert.True(DateTime.UtcNow < deadline, "The existing shell window did not provide a GPU frame.");
                    if (!source.IsReady([window])) System.Threading.Thread.Sleep(16);
                } while (!source.IsReady([window]));
                var pose = new GpuGlassPose((float)window.Bounds.X + 100, (float)window.Bounds.Y + 100,
                    100, 90, 1, 8, 1, 0, 0.35f, 1);
                var bounds = GpuWindowBackdropSource.SampleBounds(pose);
                using var scene = gpu.Texture((int)bounds.Width, (int)bounds.Height);
                using var target = gpu.Device.CreateRenderTargetView(scene);
                source.Compose(target, bounds, [window]);
                var composed = gpu.Read(scene, (int)bounds.Width, (int)bounds.Height);
                Assert.Equal(255, Pixel(composed, (int)bounds.Width, 50, 40)[3]);
                using var image = gpu.Device.CreateShaderResourceView(scene);
                using var output = gpu.Texture(pose.Width, pose.Height);
                using var outputTarget = gpu.Device.CreateRenderTargetView(output);
                using var glass = new GpuGlassPipeline(gpu.Device, gpu.Context);
                var constants = GpuGlassConstants.Create(pose, bounds, ModeRotation.Identity);
                constants.Ripple.W = 1;
                glass.Draw(outputTarget, image, constants, pose.Width, pose.Height);
                Assert.Equal(255, Pixel(gpu.Read(output, pose.Width, pose.Height), pose.Width, 50, 40)[3]);
            }
            finally { RoUninitialize(); }
        });
    }

    [Fact]
    public void RearWindowSelectionExcludesSelfOtherMiniCWindowsAndWindowsAboveTheHost()
    {
        var windows = new[]
        {
            new BackdropWindow(1, new Rect(0, 0, 100, 100), 8), // Above the host.
            new BackdropWindow(2, Rect.Empty, 7), // Host.
            new BackdropWindow(3, new Rect(0, 0, 100, 100), 7), // Material/other MiniC host.
            new BackdropWindow(4, new Rect(10, 10, 50, 50), 8),
            new BackdropWindow(5, new Rect(-20, -20, 100, 100), 9),
            new BackdropWindow(6, new Rect(200, 200, 100, 100), 8)
        };
        var selected = BackdropWindowCatalog.Behind(windows, 2, new Rect(0, 0, 100, 100), 7);
        Assert.Equal(new nint[] { 5, 4 }, selected.Select(w => w.Handle));
        Assert.Empty(BackdropWindowCatalog.Behind(windows, 20, new Rect(0, 0, 100, 100), 7));
        Assert.False(GpuGlassRenderService.RequiresWindowSource([true, true]));
        Assert.True(GpuGlassRenderService.RequiresWindowSource([true, false, true]));
    }

    [Fact]
    public async Task CaptureInteropAndAffinityWorkWithoutStartingACaptureOrShowingAWindow()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var window = new Window { Width = 240, Height = 200 };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                Assert.False(window.IsVisible);
                using (new WindowCaptureExclusion(handle))
                {
                    Assert.True(WindowCaptureExclusion.TryGetAffinity(handle, out var affinity));
                    Assert.Equal(0x11u, affinity);
                }
                Assert.True(WindowCaptureExclusion.TryGetAffinity(handle, out var original));
                Assert.Equal(0u, original);
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        });

        // Read-only item creation for an existing window; no session/pool is started.
        var created = false;
        foreach (var candidate in BackdropWindowCatalog.Snapshot().Where(w => w.ProcessId != Environment.ProcessId))
        {
            try { Assert.NotNull(GpuCapturedWindow.CreateItem(candidate.Handle)); created = true; break; }
            catch (ArgumentException) { } // Shell helper windows need not be capturable.
        }
        Assert.True(created, "No existing window accepted GraphicsCaptureItem creation.");

        using var gpu = new TestGpu();
        using var texture = gpu.Texture(10, 12);
        using var dxgi = texture.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgi.NativePointer, out var raw));
        IDirect3DSurface surface;
        try { surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(raw); }
        finally { Marshal.Release(raw); }
        using (surface)
        using (var reopened = GpuCapturedWindow.OpenTexture(surface))
        {
            Assert.Equal(10u, reopened.Description.Width);
            Assert.Equal(12u, reopened.Description.Height);
        }
    }

    [Fact]
    public void GpuCompositionPreservesLayerOrderAndAlphaAndNeverPaintsWallpaperAcrossMissingWindows()
    {
        using var gpu = new TestGpu();
        using var compositor = new GpuBackdropCompositor(gpu.Device, gpu.Context);
        using var scene = gpu.Texture(80, 64);
        using var target = gpu.Device.CreateRenderTargetView(scene);
        using var blue = gpu.Texture(1, 1, 0xFF0000FF);
        using var red = gpu.Texture(1, 1, 0x80800000); // Premultiplied half red.
        using var blueSource = gpu.Device.CreateShaderResourceView(blue);
        using var redSource = gpu.Device.CreateShaderResourceView(red);
        var bounds = new Rect(-100, -50, 80, 64);
        gpu.Context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
        compositor.Draw(target, bounds, blueSource, bounds);
        compositor.Draw(target, bounds, redSource, new Rect(-90, -40, 40, 40));
        compositor.Draw(target, bounds, null, new Rect(-70, -30, 10, 10));
        var pixels = gpu.Read(scene, 80, 64);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(pixels, 80, 5, 5));
        Assert.Equal(new byte[] { 127, 0, 128, 255 }, Pixel(pixels, 80, 15, 15));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, 80, 35, 25));
        using var source = gpu.Device.CreateShaderResourceView(scene);
        using var output = gpu.Texture(80, 64);
        using var outputTarget = gpu.Device.CreateRenderTargetView(output);
        using var glass = new GpuGlassPipeline(gpu.Device, gpu.Context);
        var pose = new GpuGlassPose(-100, -50, 80, 64, 1, 8, 1, 0, 0.35f, 1);
        var constants = GpuGlassConstants.Create(pose, bounds, ModeRotation.Identity);
        constants.Ripple.W = 1;
        glass.Draw(outputTarget, source, constants, 80, 64);
        var refracted = gpu.Read(output, 80, 64);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(refracted, 80, 35, 25));
        Assert.Equal(255, Pixel(refracted, 80, 40, 42)[3]);
        var directory = Environment.GetEnvironmentVariable("MINIC_RENDER_OUTPUT");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var bitmap = BitmapSource.Create(80, 64, 96, 96, PixelFormats.Pbgra32, null, refracted, 80*4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "capture-visible-composition.png")); encoder.Save(stream);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DesktopBaseUploadsCorrectPixelsWithNegativeMonitorCoordinates(int position)
    {
        using var gpu = new TestGpu();
        var pixels = Enumerable.Repeat(0xFF2080E0u, 16*12).ToArray();
        var bitmap = BitmapSource.Create(16, 12, 96, 96, PixelFormats.Pbgra32, null, pixels, 16*4);
        bitmap.Freeze();
        var bounds = new Rect(-100, -50, 80, 64);
        var monitor = new WallpaperMonitor(bounds, bitmap);
        var scene = new WallpaperScene([monitor], System.Windows.Media.Colors.Black, position);
        using var wallpaper = new GpuWindowBackdropSource.WallpaperTexture(gpu.Device, gpu.Context, scene, monitor);
        using var targetTexture = gpu.Texture(80, 64);
        using var target = gpu.Device.CreateRenderTargetView(targetTexture);
        using var compositor = new GpuBackdropCompositor(gpu.Device, gpu.Context);
        compositor.Draw(target, bounds, wallpaper.Source, bounds);
        var rendered = gpu.Read(targetTexture, 80, 64);
        Assert.Equal(new byte[] { 224, 128, 32, 255 }, Pixel(rendered, 80, 40, 32));
        Assert.Equal(new byte[] { 224, 128, 32, 255 }, Pixel(rendered, 80, 2, 2));
    }

    private static byte[] Pixel(byte[] bytes, int width, int x, int y) => bytes.Skip((y * width+x)*4).Take(4).ToArray();

    private sealed class TestGpu : IDisposable
    {
        internal readonly ID3D11Device Device;
        internal readonly ID3D11DeviceContext Context;
        internal TestGpu() => D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out Device, out Context).CheckError();
        internal ID3D11Texture2D Texture(int width, int height, uint? fill = null)
        {
            var texture = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
            });
            if (fill is { } color) Context.UpdateSubresource(Enumerable.Repeat(color, width*height).ToArray(), texture, rowPitch: (uint)width*4);
            return texture;
        }
        internal byte[] Read(ID3D11Texture2D texture, int width, int height)
        {
            var description = texture.Description;
            description.BindFlags = BindFlags.None; description.Usage = ResourceUsage.Staging;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = Device.CreateTexture2D(description);
            Context.CopyResource(staging, texture);
            var mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = new byte[width*height*4];
                for (var row = 0; row < height; row++) Marshal.Copy(mapped.DataPointer+(int)mapped.RowPitch*row, bytes, row*width*4, width*4);
                return bytes;
            }
            finally { Context.Unmap(staging, 0); }
        }
        public void Dispose() { Context.ClearState(); Context.Dispose(); Device.Dispose(); }
    }
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint surface, out nint result);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint handle, uint key, byte alpha, uint flags);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
}
