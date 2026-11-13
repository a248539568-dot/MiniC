using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Rect = System.Windows.Rect;

namespace MiniC.Services;

/// <summary>One GPU thread and one duplication per required output, shared by all glass windows.</summary>
internal sealed class GpuGlassRenderService
{
    private static readonly Lazy<GpuGlassRenderService> Instance = new(() => new GpuGlassRenderService());
    internal static GpuGlassRenderService Shared => Instance.Value;
    internal sealed record Statistics(int Clients, int CapturedOutputs, long Copies, long Presents)
    {
        internal int RenderingClients { get; init; }
    }
    private Statistics _statistics = new(0, 0, 0, 0);
    private long _presents;
    internal Statistics Status => Volatile.Read(ref _statistics);
    private int _outputGeneration;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly AutoResetEvent _wake = new(false);

    internal static void InvalidateOutputsIfRunning()
    {
        if (!Instance.IsValueCreated) return;
        Interlocked.Increment(ref Instance.Value._outputGeneration);
        Instance.Value._wake.Set();
    }

    private GpuGlassRenderService()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "MiniC GPU crystal" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void WakeForFrame() => _wake.Set();

    internal Subscription Register(Dispatcher dispatcher, Action<nint, bool> present, Func<GpuGlassPose, bool>? commit = null,
        nint foreground = 0, bool hideFromCapture = true)
    {
        var subscription = new Subscription(this, dispatcher, present, commit, foreground, hideFromCapture);
        lock (_gate) _subscriptions.Add(subscription);
        _wake.Set();
        return subscription;
    }

    internal sealed class Subscription : IDisposable
    {
        private readonly GpuGlassRenderService _owner;
        private readonly Dispatcher _dispatcher;
        private readonly Action<nint, bool> _present;
        private readonly Func<GpuGlassPose, bool>? _commit;
        private int _commitPending;
        private GpuGlassPose? _pose;
        private Exception? _presentationError;
        private int _generation;
        private sealed record CapturePolicy(bool Hidden, Action? Ready);
        private CapturePolicy _capturePolicy;
        internal volatile bool Disposed;
        internal nint Foreground { get; }
        internal bool HideFromCapture => Volatile.Read(ref _capturePolicy).Hidden;
        internal GpuGlassPose? Pose => Volatile.Read(ref _pose);
        internal bool CommitPending => Volatile.Read(ref _commitPending) != 0;
        internal Subscription(GpuGlassRenderService owner, Dispatcher dispatcher, Action<nint, bool> present, Func<GpuGlassPose, bool>? commit,
            nint foreground, bool hideFromCapture)
        { _owner = owner; _dispatcher = dispatcher; _present = present; _commit = commit; Foreground = foreground; _capturePolicy = new(hideFromCapture, null); }

        internal void SetCapturePolicy(bool hidden, Action? ready)
        {
            Interlocked.Exchange(ref _capturePolicy, new CapturePolicy(hidden, ready));
            _owner._wake.Set();
        }

        internal Action? CaptureCommit(bool windowSource)
        {
            var policy = Volatile.Read(ref _capturePolicy);
            if (policy.Ready is null || (!policy.Hidden && !windowSource)) return null;
            return () =>
            {
                if (Disposed || !ReferenceEquals(Interlocked.CompareExchange(ref _capturePolicy,
                    policy with { Ready = null }, policy), policy)) return;
                policy.Ready();
            };
        }

        internal void Update(GpuGlassPose pose)
        {
            if (Interlocked.Exchange(ref _pose, pose) != pose) _owner._wake.Set();
        }

        internal bool CanCommit(GpuGlassPose pose) => _commit is not null && pose.Move is not null;
        internal void RecordPresent() => Interlocked.Increment(ref _owner._presents);

        internal void Publish(IDXGISwapChain1? swapChain, GpuGlassPose? frame = null, bool attach = true,
            Action<nint>? presentFrame = null, Action? attached = null, Action? captureReady = null)
        {
            if (!attach && (_commit is null || frame?.Move is null) && captureReady is null) return;
            var generation = Interlocked.Increment(ref _generation);
            Volatile.Write(ref _commitPending, generation);
            var pointer = swapChain?.NativePointer ?? 0;
            if (pointer != 0) Marshal.AddRef(pointer);
            if (_dispatcher.HasShutdownStarted)
            {
                if (pointer != 0) Marshal.Release(pointer);
                CompleteCommit(generation);
                return;
            }
            // Keep the COM reference until the UI accepts it, even if the GPU device is reset.
            DispatcherOperation operation;
            try
            {
                operation = _dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (Disposed || generation != Volatile.Read(ref _generation)) return;
                        if (presentFrame is not null)
                        {
                            if (frame is null || _commit?.Invoke(frame) != true) return;
                            presentFrame(pointer);
                            RecordPresent();
                        }
                        if (attach)
                        {
                            _present(pointer, pointer != 0);
                            attached?.Invoke();
                        }
                        captureReady?.Invoke();
                    }
                    catch (Exception error)
                    {
                        Volatile.Write(ref _presentationError, error);
                        try { _present(0, false); }
                        catch (Exception cleanupError)
                        { MiniCLogger.Warning(nameof(GpuGlassRenderService), $"Could not detach failed glass composition: {cleanupError.Message}"); }
                    }
                    finally { CompleteCommit(generation); }
                }, DispatcherPriority.Render);
            }
            catch
            {
                if (pointer != 0) Marshal.Release(pointer);
                CompleteCommit(generation);
                if (_dispatcher.HasShutdownStarted) return;
                throw;
            }
            _ = operation.Task.ContinueWith(task =>
            {
                if (pointer != 0) Marshal.Release(pointer);
                CompleteCommit(generation);
                if (task.IsFaulted) MiniCLogger.Warning(nameof(GpuGlassRenderService), $"Glass composition failed: {task.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
        }

        private void CompleteCommit(int generation)
        {
            Interlocked.CompareExchange(ref _commitPending, 0, generation);
            _owner._wake.Set();
        }

        internal void ThrowIfPresentationFailed()
        {
            var error = Interlocked.Exchange(ref _presentationError, null);
            if (error is not null) throw new InvalidOperationException("Could not attach glass composition.", error);
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            lock (_owner._gate) _owner._subscriptions.Remove(this);
            _owner._wake.Set();
        }
    }

    private void Run()
    {
        Marshal.ThrowExceptionForHR(RoInitialize(1));
        using var idleWait = new GpuFrameWait();
        var catalog = new BackdropWindowSnapshot(BackdropWindowCatalog.Snapshot);
        var adapters = new List<Adapter>();
        var renderers = new Dictionary<Subscription, Renderer>();
        var retryAfter = DateTime.MinValue;
        var lastLog = DateTime.MinValue;
        var topology = -1;
        long copies = 0;
        while (true)
        {
            Subscription[] active;
            lock (_gate) active = _subscriptions.Where(s => !s.Disposed && s.Pose is not null).ToArray();
            var poses = active.ToDictionary(s => s, s => s.Pose!);
            var rendering = active;
            var pollMilliseconds = 100;
            try
            {
                if (topology != Volatile.Read(ref _outputGeneration))
                {
                    topology = Volatile.Read(ref _outputGeneration);
                    foreach (var subscription in renderers.Keys) subscription.Publish(null);
                    foreach (var renderer in renderers.Values) renderer.Dispose();
                    renderers.Clear();
                    foreach (var adapter in adapters) adapter.Dispose();
                    adapters.Clear();
                    retryAfter = DateTime.MinValue;
                }
                foreach (var stale in renderers.Keys.Except(active).ToArray())
                { renderers[stale].Dispose(); renderers.Remove(stale); }
                if (active.Length == 0)
                {
                    foreach (var adapter in adapters) adapter.Dispose();
                    adapters.Clear();
                    Volatile.Write(ref _statistics, new Statistics(0, 0, copies, Interlocked.Read(ref _presents)));
                    _wake.WaitOne();
                    continue;
                }
                if (DateTime.UtcNow < retryAfter) { _wake.WaitOne(100); continue; }
                var windowSnapshot = catalog.Get(Environment.TickCount64, poses.Values.Any(pose => pose.Move is not null));
                rendering = active.Where(subscription => poses[subscription].Move is not null ||
                    BackdropWindowCatalog.IsExposed(windowSnapshot, subscription.Foreground,
                        new Rect(poses[subscription].X, poses[subscription].Y, poses[subscription].Width, poses[subscription].Height),
                        (uint)Environment.ProcessId)).ToArray();
                foreach (var suspended in renderers.Where(pair => !rendering.Contains(pair.Key)))
                    suspended.Value.InvalidateSource();
                if (rendering.Length == 0)
                {
                    foreach (var adapter in adapters)
                        lock (adapter.Gate)
                        {
                            adapter.WindowSource?.Dispose(); adapter.WindowSource = null;
                            foreach (var output in adapter.Outputs) output.ReleaseCapture();
                        }
                    Volatile.Write(ref _statistics, new Statistics(active.Length, 0, copies, Interlocked.Read(ref _presents)));
                    _wake.WaitOne(100);
                    continue;
                }
                if (adapters.Count == 0) adapters = EnumerateAdapters();
                var windowSource = RequiresWindowSource(active.Select(s => s.HideFromCapture));
                pollMilliseconds = windowSource ? 100 : 4;
                var rearWindows = rendering.ToDictionary(s => s, s => windowSource
                    ? BackdropWindowCatalog.Behind(windowSnapshot, s.Foreground, GpuWindowBackdropSource.SampleBounds(poses[s]), (uint)Environment.ProcessId)
                    : Array.Empty<BackdropWindow>());
                foreach (var adapter in adapters) foreach (var output in adapter.Outputs) output.Required = false;
                foreach (var subscription in rendering)
                {
                    subscription.ThrowIfPresentationFailed();
                    var pose = poses[subscription];
                    var bounds = new Rect(pose.X, pose.Y, pose.Width, pose.Height);
                    var selected = adapters.SelectMany(a => a.Outputs.Select(o => (Adapter: a, Output: o)))
                        .OrderByDescending(pair => IntersectionArea(bounds, pair.Output.Bounds))
                        .ThenBy(pair => DistanceSquared(bounds, pair.Output.Bounds)).First();
                    if (renderers.TryGetValue(subscription, out var renderer) && renderer.Adapter != selected.Adapter)
                    { renderer.Dispose(); renderers.Remove(subscription); renderer = null; }
                    if (renderer is null)
                    { renderer = new Renderer(selected.Adapter); renderers.Add(subscription, renderer); }
                    foreach (var output in renderer.Adapter.Outputs)
                        if (!windowSource && output.Bounds.IntersectsWith(bounds)) output.Required = true;
                }
                foreach (var adapter in adapters)
                    lock (adapter.Gate)
                    {
                        if (windowSource)
                        {
                            adapter.WindowSource ??= new GpuWindowBackdropSource(adapter.Device, adapter.Context, WakeForFrame);
                            adapter.WindowSource.Prepare(rendering.Where(s => renderers[s].Adapter == adapter).SelectMany(s => rearWindows[s]));
                        }
                        else { adapter.WindowSource?.Dispose(); adapter.WindowSource = null; }
                    }
                foreach (var adapter in adapters)
                    lock (adapter.Gate)
                    foreach (var output in adapter.Outputs)
                        if (output.Required)
                        {
                            var previous = output.Version;
                            output.Capture();
                            if (previous != output.Version) copies++;
                        }
                        else output.ReleaseCapture();
                foreach (var subscription in rendering)
                {
                    if (subscription.Disposed) continue;
                    if (subscription.CommitPending) continue;
                    var renderer = renderers[subscription];
                    lock (renderer.Adapter.Gate)
                        renderer.Render(poses[subscription], subscription, windowSource ? rearWindows[subscription] : null);
                }
                Volatile.Write(ref _statistics, new Statistics(active.Length, adapters.Sum(a => a.Outputs.Count(o => o.HasCapture)), copies, Interlocked.Read(ref _presents))
                    { RenderingClients = rendering.Length });
            }
            catch (Exception error)
            {
                // Access loss, lock/unlock and device removal all invalidate duplicated textures.
                // Hide the old surface immediately; never display stale or cleared black frames.
                foreach (var subscription in active) subscription.Publish(null);
                foreach (var renderer in renderers.Values) renderer.Dispose();
                renderers.Clear();
                foreach (var adapter in adapters) adapter.Dispose();
                adapters.Clear();
                Volatile.Write(ref _statistics, new Statistics(active.Length, 0, copies, Interlocked.Read(ref _presents)));
                retryAfter = DateTime.UtcNow.AddSeconds(1);
                if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(10))
                {
                    lastLog = DateTime.UtcNow;
                    MiniCLogger.Warning(nameof(GpuGlassRenderService), $"GPU glass temporarily unavailable; will retry: {error.Message}");
                }
            }
            idleWait.Wait(_wake, renderers.Where(pair => rendering.Contains(pair.Key)).Select(pair => pair.Value.WaitingSwapChain)
                .OfType<GpuGlassSwapChain>().ToArray(), pollMilliseconds);
        }
    }

    // A visible glass window would also contaminate every hidden client's full-screen source.
    internal static bool RequiresWindowSource(IEnumerable<bool> captureHidden) => captureHidden.Any(hidden => !hidden);

    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);

    private static double IntersectionArea(Rect left, Rect right)
    { var intersection = Rect.Intersect(left, right); return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height; }
    private static double DistanceSquared(Rect left, Rect right)
    { var x = left.X + left.Width / 2 - (right.X + right.Width / 2); var y = left.Y + left.Height / 2 - (right.Y + right.Height / 2); return x * x + y * y; }

    private static List<Adapter> EnumerateAdapters()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var adapters = new List<Adapter>();
        try
        {
            for (uint index = 0; factory.EnumAdapters1(index, out var native).Success; index++)
            {
                using (native)
                {
                    var outputs = new List<IDXGIOutput1>();
                    try
                    {
                        for (uint number = 0; native.EnumOutputs(number, out var output).Success; number++)
                        {
                            using (output)
                                if (output.Description.AttachedToDesktop) outputs.Add(output.QueryInterface<IDXGIOutput1>());
                        }
                        if (outputs.Count != 0) adapters.Add(new Adapter(native, outputs));
                    }
                    catch { foreach (var output in outputs) output.Dispose(); throw; }
                }
            }
            if (adapters.Count == 0) throw new InvalidOperationException("No desktop GPU output is available.");
            return adapters;
        }
        catch { foreach (var adapter in adapters) adapter.Dispose(); throw; }
    }

    private sealed class Adapter : IDisposable
    {
        internal readonly object Gate = new();
        internal readonly ID3D11Device Device;
        internal readonly ID3D11DeviceContext Context;
        internal readonly GpuGlassPipeline Pipeline;
        internal readonly Output[] Outputs;
        internal GpuWindowBackdropSource? WindowSource;
        internal Adapter(IDXGIAdapter1 adapter, List<IDXGIOutput1> outputs)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out Device, out Context).CheckError();
            try
            {
                Pipeline = new GpuGlassPipeline(Device, Context);
                Outputs = outputs.Select(o => new Output(this, o)).ToArray();
            }
            catch { Pipeline?.Dispose(); Context.Dispose(); Device.Dispose(); throw; }
        }
        public void Dispose()
        {
            lock (Gate)
            {
                WindowSource?.Dispose();
                foreach (var output in Outputs) output.Dispose();
                Context.ClearState(); Pipeline.Dispose(); Context.Dispose(); Device.Dispose();
            }
        }
    }

    private sealed class Output : IDisposable
    {
        private readonly Adapter _adapter;
        private readonly IDXGIOutput1 _output;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _texture;
        internal ID3D11ShaderResourceView? Source;
        internal readonly Rect Bounds;
        internal readonly ModeRotation Rotation;
        internal bool Required;
        internal long Version;
        internal bool HasCapture => _duplication is not null;

        internal Output(Adapter adapter, IDXGIOutput1 output)
        {
            _adapter = adapter; _output = output;
            var description = output.Description;
            var bounds = description.DesktopCoordinates;
            Bounds = new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            Rotation = description.Rotation;
        }

        internal void Capture()
        {
            _duplication ??= _output.DuplicateOutput(_adapter.Device);
            var result = _duplication.AcquireNextFrame(0, out var info, out var resource);
            if (result.Code == unchecked((int)0x887A0027)) return; // DXGI_ERROR_WAIT_TIMEOUT: retain the last GPU frame.
            result.CheckError();
            try
            {
                using (resource)
                {
                    if (Source is not null && info.LastPresentTime == 0) return; // Pointer-only update.
                    using var frame = resource.QueryInterface<ID3D11Texture2D>();
                    if (_texture is null)
                    {
                        var description = frame.Description;
                        description.Usage = ResourceUsage.Default;
                        description.BindFlags = BindFlags.ShaderResource;
                        description.CPUAccessFlags = CpuAccessFlags.None;
                        description.MiscFlags = ResourceOptionFlags.None;
                        _texture = _adapter.Device.CreateTexture2D(description);
                        Source = _adapter.Device.CreateShaderResourceView(_texture);
                    }
                    _adapter.Context.CopyResource(_texture, frame);
                    Version++;
                }
            }
            finally { _duplication.ReleaseFrame().CheckError(); }
        }

        internal void ReleaseCapture()
        {
            Source?.Dispose(); Source = null;
            _texture?.Dispose(); _texture = null;
            _duplication?.Dispose(); _duplication = null;
        }
        public void Dispose() { ReleaseCapture(); _output.Dispose(); }
    }

    private sealed class Renderer : IDisposable
    {
        internal readonly Adapter Adapter;
        private GpuGlassSwapChain? _swapChain;
        private ID3D11RenderTargetView? _target;
        private int _width, _height;
        private GpuGlassPose? _lastPose;
        private long _lastVersion = -1;
        private bool _windowSource;
        private ID3D11Texture2D? _scene;
        private ID3D11RenderTargetView? _sceneTarget;
        private ID3D11ShaderResourceView? _sceneSource;
        private volatile bool _published;
        internal GpuGlassSwapChain? WaitingSwapChain => _swapChain?.WaitingForFrame == true ? _swapChain : null;
        internal Renderer(Adapter adapter) => Adapter = adapter;
        internal void InvalidateSource() => _lastVersion = long.MinValue;

        internal bool Render(GpuGlassPose pose, Subscription subscription, BackdropWindow[]? windows)
        {
            var windowSource = windows is not null;
            // Keep the previously presented surface and its exclusion until the new source has pixels.
            if (windowSource && !Adapter.WindowSource!.IsReady(windows!)) return false;
            var captureCommit = subscription.CaptureCommit(windowSource);
            var bounds = new Rect(pose.X, pose.Y, pose.Width, pose.Height);
            var outputs = Adapter.Outputs.Where(o => o.Source is not null && o.Bounds.IntersectsWith(bounds)).ToArray();
            var version = windowSource ? Adapter.WindowSource!.Version(windows!) : outputs.Sum(o => o.Version);
            if (!windowSource && outputs.Length == 0)
            {
                // DXGI may time out before its first frame after a source switch.
                // Retain the valid WGC presentation until replacement pixels arrive.
                return false;
            }
            if (_published && _lastPose == pose && _lastVersion == version && _windowSource == windowSource && captureCommit is null) return false;
            if (_swapChain is null)
            {
                _swapChain = new GpuGlassSwapChain(Adapter.Device, pose.Width, pose.Height, Adapter.Gate);
                _width = pose.Width; _height = pose.Height;
            }
            if (!_swapChain.TryBeginFrame()) return false;
            if (_width != pose.Width || _height != pose.Height)
            {
                _target?.Dispose(); _target = null;
                _swapChain.Resize(pose.Width, pose.Height);
                _width = pose.Width; _height = pose.Height;
            }
            if (_target is null)
            {
                using var buffer = _swapChain.Native.GetBuffer<ID3D11Texture2D>(0);
                _target = Adapter.Device.CreateRenderTargetView(buffer);
            }
            Adapter.Context.ClearRenderTargetView(_target, new Color4(0, 0, 0, 0));
            if (windowSource)
            {
                var sampleBounds = GpuWindowBackdropSource.SampleBounds(pose);
                if (_scene is null || _scene.Description.Width != sampleBounds.Width || _scene.Description.Height != sampleBounds.Height)
                {
                    ReleaseScene();
                    _scene = Adapter.Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)sampleBounds.Width, Height = (uint)sampleBounds.Height, MipLevels = 1, ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
                    });
                    _sceneTarget = Adapter.Device.CreateRenderTargetView(_scene);
                    _sceneSource = Adapter.Device.CreateShaderResourceView(_scene);
                }
                Adapter.WindowSource!.Compose(_sceneTarget!, sampleBounds, windows!);
                var constants = GpuGlassConstants.Create(pose, sampleBounds, ModeRotation.Identity);
                constants.Ripple.W = 1; // Unlike DXGI, the assembled scene has meaningful alpha.
                Adapter.Pipeline.Draw(_target, _sceneSource!, constants, _width, _height);
            }
            else
            {
                ReleaseScene();
                foreach (var output in outputs)
                    Adapter.Pipeline.Draw(_target, output.Source!, GpuGlassConstants.Create(pose, output.Bounds, output.Rotation), _width, _height);
            }
            if (subscription.CanCommit(pose))
            {
                Adapter.Context.Flush();
                subscription.Publish(_swapChain.Native, pose, attach: !_published, presentFrame: _swapChain.Present,
                    attached: () => _published = true, captureReady: captureCommit);
            }
            else
            {
                _swapChain.Present(_swapChain.Native.NativePointer);
                subscription.RecordPresent();
                subscription.Publish(_published ? null : _swapChain.Native, pose, attach: !_published,
                    attached: () => _published = true, captureReady: captureCommit);
            }
            _lastPose = pose; _lastVersion = version; _windowSource = windowSource;
            return true;
        }
        public void Dispose()
        {
            lock (Adapter.Gate) { ReleaseScene(); _target?.Dispose(); _swapChain?.Dispose(); }
        }
        private void ReleaseScene()
        { _sceneSource?.Dispose(); _sceneSource = null; _sceneTarget?.Dispose(); _sceneTarget = null; _scene?.Dispose(); _scene = null; }
    }
}
