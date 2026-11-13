using System.Runtime.InteropServices;
using System.Windows;
using Windows.UI.Composition;

namespace MiniC.Services;

/// <summary>System-diffused live background, reserved for the frosted material.</summary>
internal sealed class ComposedFrostedBackdrop : IDisposable
{
    private readonly CompositionBackdropHost _host;
    private readonly CompositionBackdropBrush _backdrop = null!;
    private readonly CompositionEffectBrush _diffusion = null!;
    private WindowCaptureExclusion? _backgroundExclusion;
    public bool HideFromCapture { get; private set; }
    private double _blurRadius;
    private float _appliedDeviation = -1;
    private bool _disposed;
    private ComposedFrostedBackdrop(Window foreground, FrameworkElement surface, double radius, double blurRadius,
        bool hideFromCapture)
    {
        Radius = radius;
        _blurRadius = blurRadius;
        HideFromCapture = hideFromCapture;
        _host = new CompositionBackdropHost(foreground, surface);
        try
        {
            if (hideFromCapture)
            {
                _backgroundExclusion = new WindowCaptureExclusion(_host.Handle);
            }
            _backdrop = _host.Compositor.CreateHostBackdropBrush();
            _diffusion = CompositionGaussianBlur.CreateBrush(_host.Compositor, _backdrop, blurRadius);
            _host.Root.Brush = _diffusion;
            _host.Root.Opacity = 1;
            if (!Align()) throw new InvalidOperationException("Could not align frosted backdrop.");
        }
        catch
        {
            try { _host.Dispose(); }
            finally
            {
                try { _diffusion?.Dispose(); }
                finally { _backdrop?.Dispose(); _backgroundExclusion?.Dispose(); }
            }
            throw;
        }
    }
    public static ComposedFrostedBackdrop? TryCreate(Window foreground, FrameworkElement surface, double radius,
        double blurRadius, bool hideFromCapture = true)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return null;
        try { return new ComposedFrostedBackdrop(foreground, surface, radius, blurRadius, hideFromCapture); }
        catch (Exception error) when (error is COMException or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException or InvalidOperationException or UnauthorizedAccessException
            or TypeLoadException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException or InvalidCastException)
        {
            MiniCLogger.Warning(nameof(ComposedFrostedBackdrop), $"Host backdrop unavailable: {error.Message}");
            return null;
        }
    }
    public double Radius { get; set; }
    internal bool SetHideFromCapture(bool hidden)
    {
        if (_disposed) return false;
        if (HideFromCapture == hidden) return true;
        try
        {
            if (hidden) _backgroundExclusion ??= new WindowCaptureExclusion(_host.Handle);
            else { _backgroundExclusion?.Dispose(); _backgroundExclusion = null; }
            HideFromCapture = hidden;
            return true;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            MiniCLogger.Warning(nameof(ComposedFrostedBackdrop), $"Could not update frosted capture policy: {error.Message}");
            return false;
        }
    }
    public double ForegroundOpacity
    {
        set { if (!_disposed) _host.Root.Opacity = (float)Math.Clamp(double.IsFinite(value) ? value : 1, 0, 1); }
    }
    public double BlurRadius { set { _blurRadius = value; UpdateBlur(); } }
    public bool Align()
    {
        if (!_host.Align(Radius)) return false;
        UpdateBlur();
        return true;
    }
    private void UpdateBlur()
    {
        var deviation = CompositionGaussianBlur.StandardDeviation(_blurRadius) * (float)_host.Scale;
        if (_appliedDeviation == deviation) return;
        _diffusion.Properties.InsertScalar("Diffusion.BlurAmount", deviation);
        _appliedDeviation = deviation;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _host.Dispose(); }
        finally
        {
            try { _diffusion.Dispose(); }
            finally
            {
                try { _backdrop.Dispose(); }
                finally { _backgroundExclusion?.Dispose(); }
            }
        }
    }
}
