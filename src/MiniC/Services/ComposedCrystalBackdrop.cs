using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MiniC.Controls;

namespace MiniC.Services;

/// <summary>UI-side lifetime and placement; all background texture work stays on the GPU thread.</summary>
internal sealed class ComposedCrystalBackdrop : IDisposable
{
    private readonly CompositionBackdropHost _host;
    private readonly CrystalGlassSurface _surface;
    private readonly nint _foreground;
    private WindowCaptureExclusion? _backgroundExclusion;
    private readonly Action? _captureReady;
    internal bool HideFromCapture { get; private set; }
    internal bool CapturePolicyReady { get; private set; }
    private readonly GpuGlassRenderService.Subscription _subscription;
    private bool _disposed, _available;
    private double _opacity = 1;
    private GlassWindowMove? _requestedMove;
    private GpuGlassPose? _latestPose;
    private long _moveSession;
    private bool _committingMove;

    private ComposedCrystalBackdrop(Window foreground, CrystalGlassSurface surface, bool hideFromCapture, Action? captureReady)
    {
        _surface = surface;
        _captureReady = captureReady;
        HideFromCapture = hideFromCapture;
        _foreground = new WindowInteropHelper(foreground).Handle;
        _host = new CompositionBackdropHost(foreground, surface);
        try
        {
            _host.Root.Opacity = 0;
            _subscription = GpuGlassRenderService.Shared.Register(foreground.Dispatcher, (swapChain, available) =>
            {
                if (_disposed) return;
                if (available) _host.AttachSwapChain(swapChain);
                else _host.DetachSwapChain();
                _available = available;
                _host.Root.Opacity = available ? (float)_opacity : 0;
                if (available) Align();
                else _host.SetVisible(false);
                if (!available) FinishMove();
            }, CommitFrame, _foreground, hideFromCapture);
            RequestCapturePolicy(hideFromCapture);
            if (!Align()) throw new InvalidOperationException("Could not place GPU glass surface.");
        }
        catch { Dispose(); throw; }
    }

    internal static ComposedCrystalBackdrop? TryCreate(Window foreground, CrystalGlassSurface surface,
        bool hideFromCapture = true, Action? captureReady = null)
    {
        try { return new ComposedCrystalBackdrop(foreground, surface, hideFromCapture, captureReady); }
        catch (Exception error) when (error is COMException or Win32Exception or InvalidOperationException
            or NotSupportedException or UnauthorizedAccessException)
        {
            MiniCLogger.Warning(nameof(ComposedCrystalBackdrop), $"GPU glass could not start: {error.Message}");
            return null;
        }
    }

    internal bool SetHideFromCapture(bool hidden)
    {
        if (_disposed) return false;
        if (HideFromCapture == hidden) return true;
        try { RequestCapturePolicy(hidden); return true; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MiniCLogger.Warning(nameof(ComposedCrystalBackdrop), $"Could not update crystal capture policy: {error.Message}");
            return false;
        }
    }

    private void RequestCapturePolicy(bool hidden)
    {
        if (hidden)
        {
            if (!WindowCaptureExclusion.TryGetAffinity(_foreground, out var affinity) || affinity != 0x11)
                throw new InvalidOperationException("Screen capture requires the foreground to be excluded before GPU glass starts.");
            _backgroundExclusion ??= new WindowCaptureExclusion(_host.Handle);
        }
        else GpuCapturedWindow.RequestBorderlessAccess();
        HideFromCapture = hidden;
        CapturePolicyReady = false;
        _subscription.SetCapturePolicy(hidden, () =>
        {
            if (_disposed) return;
            if (!hidden) { _backgroundExclusion?.Dispose(); _backgroundExclusion = null; }
            CapturePolicyReady = true;
            _captureReady?.Invoke();
        });
    }

    internal double ForegroundOpacity
    {
        set
        {
            _opacity = Math.Clamp(double.IsFinite(value) ? value : 1, 0, 1);
            if (!_disposed) _host.Root.Opacity = _available ? (float)_opacity : 0;
        }
    }

    internal bool Align()
    {
        if (_committingMove) return true;
        if (_disposed || !_host.Align(_surface.Radius, _available)) return false;
        var bounds = _host.Bounds;
        if (_requestedMove is { } move && NativeWindowPlacement.TryGetBounds(_foreground, out var foregroundBounds))
            bounds.Offset(move.X - foregroundBounds.X, move.Y - foregroundBounds.Y);
        static float Finite(double value, double fallback, double min, double max) =>
            (float)Math.Clamp(double.IsFinite(value) ? value : fallback, min, max);
        _latestPose = new GpuGlassPose((float)bounds.X, (float)bounds.Y, (int)bounds.Width, (int)bounds.Height,
            Finite(_host.Scale, 1, 0.01, 16), Finite(_surface.Radius, GroupGeometry.Radius, 0, 1000),
            Finite(_surface.Strength, 1, 0, 8), Finite(_surface.Angle, 0, -360, 360),
            Finite(_surface.RippleStrength, 0.35, 0, 8), Finite(_surface.RippleScale, 1, 0.01, 32)) { Move = _requestedMove };
        _subscription.Update(_latestPose);
        return true;
    }

    internal bool RequestMove(int x, int y)
    {
        if (_disposed || !_available) return false;
        _requestedMove = new GlassWindowMove(x, y, _moveSession);
        return Align();
    }

    private bool CommitFrame(GpuGlassPose frame)
    {
        if (_disposed || frame.Move is not { } move || move.Session != _moveSession || _requestedMove is null) return false;
        CommitMove(frame);
        // Preserve newer input that arrived while the GPU was drawing this frame.
        Align();
        return true;
    }

    private void CommitMove(GpuGlassPose frame)
    {
        _committingMove = true;
        try { _host.CommitMove(frame); }
        finally { _committingMove = false; }
    }

    internal void FinishMove(bool applyLatest = true)
    {
        if (_requestedMove is null) return;
        var final = _latestPose;
        _requestedMove = null;
        _moveSession++;
        if (applyLatest && final is not null && NativeWindowPlacement.TryGetBounds(_foreground, out _)) CommitMove(final);
        if (!_disposed) Align();
    }

    public void Dispose()
    {
        if (_disposed) return;
        FinishMove();
        _disposed = true;
        _subscription?.Dispose();
        // Remove the native visual before making the foreground capturable again.
        try { _host.Dispose(); }
        finally { _backgroundExclusion?.Dispose(); }
    }

}
