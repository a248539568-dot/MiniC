using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniC.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;
using Rect = System.Windows.Rect;

namespace MiniC.Tests;

[Collection("Shared desktop renderer")]
public sealed class NativeCrystalTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ShaderDisplacesWholePaneAndPreservesPremultipliedCorners(double scale)
    {
        using var gpu = new OffscreenGpu();
        var pose = Pose(scale);
        var plain = gpu.Render(pose with { Strength = 0, RippleStrength = 0 });
        var glass = gpu.Render(pose);
        var width = pose.Width; var height = pose.Height;
        Assert.NotEqual(Pixel(plain, width, width / 2, height / 2), Pixel(glass, width, width / 2, height / 2));
        Assert.True(Difference(plain, glass) > width * height / 10);
        Assert.Equal(0, Pixel(glass, width, 0, 0)[3]);
        Assert.Equal(255, Pixel(glass, width, width / 2, height / 2)[3]);
        for (var index = 0; index < glass.Length; index += 4)
            Assert.True(glass[index] <= glass[index + 3] && glass[index + 1] <= glass[index + 3] && glass[index + 2] <= glass[index + 3]);
        Export(glass, width, height, $"native-crystal-{scale}.png");
    }

    [Fact]
    public void MovingOverAnUnchangedGpuTextureUpdatesSamplingAndOpticalParameters()
    {
        using var gpu = new OffscreenGpu();
        var pose = Pose(1);
        var original = gpu.Render(pose);
        var moved = gpu.Render(pose with { X = pose.X + 37, Y = pose.Y + 21 });
        Assert.True(Difference(original, moved) > pose.Width * pose.Height * 0.7);
        var changed = gpu.Render(pose with { Angle = 75, Strength = 1.6f, RippleStrength = 0.8f, RippleScale = 2 });
        Assert.True(Difference(original, changed) > pose.Width * pose.Height / 10);
        Assert.NotEqual(Pixel(original, pose.Width, 120, 100), Pixel(changed, pose.Width, 120, 100));
    }

    [Theory]
    [InlineData(ModeRotation.Identity)]
    [InlineData(ModeRotation.Rotate90)]
    [InlineData(ModeRotation.Rotate180)]
    [InlineData(ModeRotation.Rotate270)]
    public void RotatedOutputUsesCorrectTextureCoordinates(ModeRotation rotation)
    {
        using var gpu = new OffscreenGpu();
        var pose = Pose(1) with { Strength = 0, RippleStrength = 0 };
        var image = gpu.Render(pose, rotation);
        var uvX = (pose.X + 120.5f - OffscreenGpu.Output.X) / OffscreenGpu.Output.Width;
        var uvY = (pose.Y + 100.5f - OffscreenGpu.Output.Y) / OffscreenGpu.Output.Height;
        var (u, v) = rotation switch
        {
            ModeRotation.Rotate90 => (uvY, 1 - uvX),
            ModeRotation.Rotate180 => (1 - uvX, 1 - uvY),
            ModeRotation.Rotate270 => (1 - uvY, uvX),
            _ => (uvX, uvY)
        };
        var pixel = Pixel(image, pose.Width, 120, 100);
        Assert.InRange((int)pixel[2], (int)Math.Round(u * 255) - 2, (int)Math.Round(u * 255) + 2);
        Assert.InRange((int)pixel[1], (int)Math.Round(v * 255) - 2, (int)Math.Round(v * 255) + 2);
    }

    [Fact]
    public async Task HiddenNativeSurfaceAcceptsTheGpuSwapChainAndRestoresCaptureAffinity()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var foreground = new Window { Width = 240, Height = 200, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent };
            var handle = new WindowInteropHelper(foreground).EnsureHandle();
            using var host = new CompositionBackdropHost(foreground, new Grid());
            Assert.False(IsWindowVisible(handle));
            Assert.False(IsWindowVisible(host.Handle));
            var activated = GetForegroundWindow();
            Assert.True(NativeWindowPlacement.TryGetBounds(handle, out var original));
            for (var index = 0; index < 80; index++)
            {
                var move = new GlassWindowMove(60 + index * 3, 90 + index * 2, 1);
                var material = new Rect(move.X + 5, move.Y + 7, 230, 180);
                NativeWindowPlacement.MovePair(handle, host.Handle, move, material);
                Assert.True(NativeWindowPlacement.TryGetBounds(handle, out var actual));
                Assert.Equal(new Point(move.X, move.Y), actual.TopLeft);
                Assert.Equal(original.Size, actual.Size);
                Assert.True(NativeWindowPlacement.TryGetBounds(host.Handle, out var background));
                Assert.Equal(material, background);
            }
            Assert.Equal(activated, GetForegroundWindow());
            Assert.False(IsWindowVisible(handle));
            Assert.False(IsWindowVisible(host.Handle));
            using (new WindowCaptureExclusion(handle))
            using (new WindowCaptureExclusion(host.Handle))
            {
                Assert.True(WindowCaptureExclusion.TryGetAffinity(handle, out var affinity));
                Assert.Equal(0x11u, affinity);
                using var gpu = new OffscreenGpu();
                using var swapChain = new GpuGlassSwapChain(gpu.Device, 240, 200);
                using (var native = swapChain.Native.QueryInterface<IDXGISwapChain2>())
                {
                    Assert.Equal(1u, native.MaximumFrameLatency);
                    Assert.True(native.Description1.Flags.HasFlag(SwapChainFlags.FrameLatencyWaitableObject));
                }
                await Until(swapChain.TryBeginFrame);
                using (var targetTexture = swapChain.Native.GetBuffer<ID3D11Texture2D>(0))
                using (var target = gpu.Device.CreateRenderTargetView(targetTexture))
                    gpu.Draw(target, Pose(1));
                swapChain.Present(swapChain.Native.NativePointer);
                host.AttachSwapChain(swapChain.Native.NativePointer);
                Assert.IsType<Windows.UI.Composition.CompositionSurfaceBrush>(host.Root.Brush);
                Assert.False(IsWindowVisible(host.Handle));
                await Until(swapChain.TryBeginFrame);
                swapChain.Resize(320, 240);
                using (var resized = swapChain.Native.GetBuffer<ID3D11Texture2D>(0))
                using (var target = gpu.Device.CreateRenderTargetView(resized))
                {
                    Assert.Equal(320u, resized.Description.Width);
                    Assert.Equal(240u, resized.Description.Height);
                    gpu.Draw(target, Pose(1) with { Width = 320, Height = 240 });
                }
                swapChain.Present(swapChain.Native.NativePointer);
                host.DetachSwapChain();
                var latencyHandle = swapChain.LatencyHandle;
                Assert.True(GetHandleInformation(latencyHandle, out _));
                swapChain.Dispose();
                Assert.False(GetHandleInformation(latencyHandle, out _));
            }
            Assert.True(WindowCaptureExclusion.TryGetAffinity(handle, out var restored));
            Assert.Equal(0u, restored);
            foreground.Close();
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SharedWorkerCapturesOneOutputForTwoHiddenClientsAndReleasesItAfterResetAndClose()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var service = GpuGlassRenderService.Shared;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFrames = 0;
            using var client1 = service.Register(dispatcher, (pointer, ready) =>
            {
                if (!ready) return;
                Assert.NotEqual(0, pointer);
                first.TrySetResult();
                if (++firstFrames > 1) restarted.TrySetResult();
            });
            using var client2 = service.Register(dispatcher, (pointer, ready) => { if (ready) second.TrySetResult(); });
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var pose = Pose(1) with { X = screen.Left + 20, Y = screen.Top + 20 };
            client1.Update(pose); client2.Update(pose with { X = pose.X + 260 });
            await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(4));
            await Until(() => service.Status.Clients == 2);
            Assert.Equal(1, service.Status.CapturedOutputs);
            Assert.True(service.Status.Copies > 0);
            var previousPresents = service.Status.Presents;
            client1.Update(pose with { X = pose.X + 32 });
            await Until(() => service.Status.Presents > previousPresents);
            GpuGlassRenderService.InvalidateOutputsIfRunning();
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            client1.Dispose(); client2.Dispose();
            await Until(() => service.Status.Clients == 0 && service.Status.CapturedOutputs == 0);
        });
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "GPU worker did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task DragFramesUseOneSnapshotCoalesceNewInputAndDiscardCancelledPendingMoves()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var service = GpuGlassRenderService.Shared;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var moves = new System.Collections.Generic.List<GpuGlassPose>();
            var accept = true;
            var attempts = 0;
            using var client = service.Register(dispatcher,
                (_, available) => { if (available) ready.TrySetResult(); }, frame =>
                {
                    Assert.True(dispatcher.CheckAccess());
                    attempts++;
                    if (!accept) return false;
                    moves.Add(frame);
                    return true;
                });
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var pose = Pose(1) with { X = screen.Left + 20, Y = screen.Top + 20 };
            client.Update(pose);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await Until(() => !client.CommitPending);
            var first = pose with { X = pose.X + 12, Y = pose.Y + 17,
                Move = new GlassWindowMove((int)pose.X + 12, (int)pose.Y + 17, 1) };
            var latest = first;
            using (dispatcher.DisableProcessing())
            {
                client.Update(first);
                Assert.True(System.Threading.SpinWait.SpinUntil(() => client.CommitPending, TimeSpan.FromSeconds(3)));
                for (var index = 1; index <= 200; index++)
                {
                    latest = first with { X = first.X + index, Y = first.Y + index / 2,
                        Move = new GlassWindowMove((int)first.X + index, (int)first.Y + index / 2, 1) };
                    client.Update(latest);
                }
            }
            await Until(() => moves.Count >= 2);
            Assert.Equal(first, moves[0]);
            Assert.Equal(latest, moves[1]);
            await Until(() => !client.CommitPending);

            var committed = moves.Count;
            var previousAttempts = attempts;
            using (dispatcher.DisableProcessing())
            {
                client.Update(latest with { X = latest.X + 5,
                    Move = latest.Move! with { X = latest.Move.X + 5 } });
                Assert.True(System.Threading.SpinWait.SpinUntil(() => client.CommitPending, TimeSpan.FromSeconds(3)));
                accept = false;
                client.Update(pose); // Gesture ended: the queued frame must not move the window.
            }
            await Until(() => attempts > previousAttempts && !client.CommitPending);
            Assert.Equal(committed, moves.Count);

            accept = true;
            var nextGesture = first with { Move = first.Move! with { Session = 2 } };
            client.Update(nextGesture);
            await Until(() => moves.Any(frame => frame == nextGesture));
            await Until(() => !client.CommitPending);
            previousAttempts = attempts;
            using (dispatcher.DisableProcessing())
            {
                client.Update(nextGesture with { X = nextGesture.X + 5 });
                Assert.True(System.Threading.SpinWait.SpinUntil(() => client.CommitPending, TimeSpan.FromSeconds(3)));
                client.Dispose();
            }
            await Until(() => !client.CommitPending && service.Status.Clients == 0 && service.Status.CapturedOutputs == 0);
            Assert.Equal(previousAttempts, attempts);
        });
    }

    [Fact]
    public void CapturePollingTimerAlsoWakesForNewInput()
    {
        using var timer = new GpuFrameWait();
        using var wake = new System.Threading.AutoResetEvent(true);
        timer.Wait(wake);
        Assert.False(wake.WaitOne(0)); // The input signal was consumed, rather than waiting for the deadline.
        timer.Wait(wake); // No input: the one-shot capture deadline must complete.
        wake.Set();
        timer.Wait(wake);
        Assert.False(wake.WaitOne(0));
    }

    [Fact]
    public async Task WorkerRecoversWhenTheUiRejectsTheFirstCompositionSurface()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var service = GpuGlassRenderService.Shared;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejected = false;
            var detached = false;
            using var client = service.Register(System.Windows.Threading.Dispatcher.CurrentDispatcher, (pointer, available) =>
            {
                if (!available) { detached = true; return; }
                if (!rejected) { rejected = true; throw new InvalidOperationException("Injected composition rejection."); }
                ready.TrySetResult();
            });
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            client.Update(Pose(1) with { X = screen.Left + 20, Y = screen.Top + 20 });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(rejected && detached);
            client.Dispose();
            await Until(() => service.Status.Clients == 0 && service.Status.CapturedOutputs == 0);
        });
    }

    private static GpuGlassPose Pose(double scale) => new(-100, -50, (int)(240 * scale), (int)(200 * scale), (float)scale, 20, 1, 0, 0.35f, 1);
    private static byte[] Pixel(byte[] bytes, int width, int x, int y) => bytes.Skip((y * width + x) * 4).Take(4).ToArray();
    private static int Difference(byte[] a, byte[] b)
    {
        var count = 0;
        for (var index = 0; index < a.Length; index += 4)
            if (Math.Abs(a[index] - b[index]) + Math.Abs(a[index + 1] - b[index + 1]) + Math.Abs(a[index + 2] - b[index + 2]) > 4) count++;
        return count;
    }

    private static void Export(byte[] bytes, int width, int height, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MINIC_RENDER_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }

    private sealed class OffscreenGpu : IDisposable
    {
        internal static readonly Rect Output = new(-200, -100, 640, 480);
        internal readonly ID3D11Device Device;
        private readonly ID3D11DeviceContext _context;
        private readonly GpuGlassPipeline _pipeline;
        private readonly ID3D11Texture2D _input;
        private readonly ID3D11ShaderResourceView _source;

        internal OffscreenGpu()
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out Device, out _context).CheckError();
            _pipeline = new GpuGlassPipeline(Device, _context);
            _input = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = 640, Height = 480, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource
            });
            var pixels = new uint[640 * 480];
            for (var y = 0; y < 480; y++) for (var x = 0; x < 640; x++)
                pixels[y * 640 + x] = (uint)(x * 255 / 639 << 16 | y * 255 / 479 << 8 | (((x / 8 + y / 8) % 2 == 0) ? 50 : 220));
            // Deliberately leave input alpha zero: desktop capture does not define it.
            _context.UpdateSubresource(pixels, _input, rowPitch: 640 * 4);
            _source = Device.CreateShaderResourceView(_input);
        }

        internal void Draw(ID3D11RenderTargetView target, GpuGlassPose pose, ModeRotation rotation = ModeRotation.Identity) =>
            _pipeline.Draw(target, _source, GpuGlassConstants.Create(pose, Output, rotation), pose.Width, pose.Height);

        internal byte[] Render(GpuGlassPose pose, ModeRotation rotation = ModeRotation.Identity)
        {
            var description = new Texture2DDescription
            {
                Width = (uint)pose.Width, Height = (uint)pose.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.RenderTarget
            };
            using var texture = Device.CreateTexture2D(description);
            using var target = Device.CreateRenderTargetView(texture);
            _context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
            Draw(target, pose, rotation);
            description.BindFlags = BindFlags.None; description.Usage = ResourceUsage.Staging; description.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = Device.CreateTexture2D(description);
            _context.CopyResource(staging, texture);
            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = new byte[pose.Width * pose.Height * 4];
                for (var row = 0; row < pose.Height; row++)
                    Marshal.Copy(mapped.DataPointer + (int)mapped.RowPitch * row, bytes, row * pose.Width * 4, pose.Width * 4);
                return bytes;
            }
            finally { _context.Unmap(staging, 0); }
        }

        public void Dispose() { _source.Dispose(); _input.Dispose(); _pipeline.Dispose(); _context.ClearState(); _context.Dispose(); Device.Dispose(); }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);
}
