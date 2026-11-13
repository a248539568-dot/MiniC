using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Rect = System.Windows.Rect;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MiniC.Services;

/// <summary>Shared window sessions and desktop base for screenshot-visible glass. GPU-thread owned.</summary>
internal sealed class GpuWindowBackdropSource : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    internal readonly GpuBackdropCompositor Compositor;
    private readonly Dictionary<nint, GpuCapturedWindow> _captures = [];
    private readonly Dictionary<nint, DateTime> _retry = [];
    private readonly Dictionary<nint, DateTime> _pending = [];
    private readonly Dictionary<WallpaperMonitor, WallpaperTexture> _wallpapers = [];
    private WallpaperScene? _wallpaper;
    private long _wallpaperVersion = -1;
    private bool _disposed;
    private readonly Action? _frameReady;

    internal GpuWindowBackdropSource(ID3D11Device device, ID3D11DeviceContext context, Action? frameReady = null)
    { _device = device; _context = context; _frameReady = frameReady; Compositor = new(device, context); }

    internal static Rect SampleBounds(GpuGlassPose pose)
    {
        var bounds = new Rect(pose.X, pose.Y, pose.Width, pose.Height);
        // Maximum 14 DIP lens bend plus dispersion, ripple and linear-filter margin.
        var padding = Math.Ceiling((16 * pose.Strength + pose.RippleStrength + 2) * pose.Scale);
        bounds.Inflate(padding, padding);
        return bounds;
    }

    internal void Prepare(IEnumerable<BackdropWindow> windows)
    {
        foreach (var capture in _captures.Values) capture.Required = false;
        var required = windows.DistinctBy(w => w.Handle).ToArray();
        var handles = required.Select(w => w.Handle).ToHashSet();
        foreach (var stale in _retry.Keys.Where(h => !handles.Contains(h)).ToArray()) _retry.Remove(stale);
        foreach (var stale in _pending.Keys.Where(h => !handles.Contains(h)).ToArray()) _pending.Remove(stale);
        foreach (var window in required)
        {
            try
            {
                if (!_captures.TryGetValue(window.Handle, out var capture))
                {
                    if (_retry.TryGetValue(window.Handle, out var deadline) && DateTime.UtcNow < deadline) continue;
                    capture = new GpuCapturedWindow(_device, _context, window.Handle, _frameReady);
                    _captures.Add(window.Handle, capture);
                    _retry.Remove(window.Handle);
                }
                capture.Required = true;
                capture.Capture();
                if (capture.SourceFor(window.Bounds) is not null) _pending.Remove(window.Handle);
                else if (!_pending.TryGetValue(window.Handle, out var deadline))
                    _pending[window.Handle] = DateTime.UtcNow.AddSeconds(2);
                else if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("Rear window did not provide a frame matching its current bounds.");
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                if (_captures.Remove(window.Handle, out var failed)) failed.Dispose();
                _pending.Remove(window.Handle);
                _retry[window.Handle] = DateTime.UtcNow.AddSeconds(2);
                MiniCLogger.Warning(nameof(GpuWindowBackdropSource), $"Rear window capture unavailable: {error.Message}");
            }
        }
        foreach (var stale in _captures.Where(pair => !pair.Value.Required).Select(pair => pair.Key).ToArray())
        { _captures[stale].Dispose(); _captures.Remove(stale); }

        var task = WallpaperTextureService.GetAsync();
        if (task.IsCompletedSuccessfully && (_wallpaper is null || _wallpaperVersion != WallpaperTextureService.Version))
        {
            foreach (var wallpaper in _wallpapers.Values) wallpaper.Dispose();
            _wallpapers.Clear();
            _wallpaper = task.Result;
            _wallpaperVersion = WallpaperTextureService.Version;
        }
        if (task.IsFaulted) _ = task.Exception;
    }

    internal int Version(IEnumerable<BackdropWindow> windows)
    {
        var hash = new HashCode(); hash.Add(_wallpaperVersion);
        foreach (var window in windows)
        {
            hash.Add(window.Handle); hash.Add(window.Bounds); hash.Add(window.Opacity);
            hash.Add(window.ColorKey.HasValue); hash.Add(window.ColorKey.GetValueOrDefault());
            hash.Add(_captures.TryGetValue(window.Handle, out var capture) ? capture.Version : -1);
        }
        return hash.ToHashCode();
    }

    internal bool IsReady(IEnumerable<BackdropWindow> windows) => _wallpaper is not null
        && windows.All(window => !_captures.TryGetValue(window.Handle, out var capture)
            || capture.SourceFor(window.Bounds) is not null);

    internal void Compose(ID3D11RenderTargetView target, Rect bounds, IEnumerable<BackdropWindow> windows)
    {
        _context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
        if (_wallpaper is not null)
            foreach (var monitor in _wallpaper.Monitors.Where(m => m.Bounds.IntersectsWith(bounds)))
            {
                if (!_wallpapers.TryGetValue(monitor, out var texture))
                {
                    texture = new WallpaperTexture(_device, _context, _wallpaper, monitor);
                    _wallpapers.Add(monitor, texture);
                }
                Compositor.Draw(target, bounds, texture.Source, monitor.Bounds);
            }
        foreach (var window in windows)
        {
            var source = _captures.TryGetValue(window.Handle, out var capture) ? capture.SourceFor(window.Bounds) : null;
            Compositor.Draw(target, bounds, source, window.Bounds, window.Opacity / 255f, window.ColorKey);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var capture in _captures.Values) capture.Dispose();
        foreach (var wallpaper in _wallpapers.Values) wallpaper.Dispose();
        Compositor.Dispose();
    }

    /// <summary>Wallpaper upload happens once per change, never as a per-frame background readback.</summary>
    internal sealed class WallpaperTexture : IDisposable
    {
        private readonly ID3D11Texture2D _texture;
        internal readonly ID3D11ShaderResourceView Source;
        internal WallpaperTexture(ID3D11Device device, ID3D11DeviceContext context, WallpaperScene scene, WallpaperMonitor monitor)
        {
            var width = (int)monitor.Bounds.Width; var height = (int)monitor.Bounds.Height;
            using var bitmap = new System.Drawing.Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(scene.Background.R, scene.Background.G, scene.Background.B));
                if (monitor.Image is { } image)
                {
                    var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
                    var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
                    converted.CopyPixels(bytes, image.PixelWidth * 4, 0);
                    using var source = new System.Drawing.Bitmap(image.PixelWidth, image.PixelHeight, PixelFormat.Format32bppPArgb);
                    var data = source.LockBits(new(0, 0, source.Width, source.Height), ImageLockMode.WriteOnly, source.PixelFormat);
                    try
                    {
                        for (var row = 0; row < source.Height; row++)
                            Marshal.Copy(bytes, row * source.Width * 4, data.Scan0 + row * data.Stride, source.Width * 4);
                    }
                    finally { source.UnlockBits(data); }
                    if (scene.Position == 1)
                    {
                        using var brush = new System.Drawing.TextureBrush(source);
                        graphics.FillRectangle(brush, 0, 0, width, height);
                    }
                    else
                    {
                        var area = scene.Position == 5 ? scene.Monitors.Select(m => m.Bounds).Aggregate(Rect.Union) : monitor.Bounds;
                        var fit = WallpaperTextureService.FitImage(area, source.Width, source.Height, scene.Position);
                        graphics.DrawImage(source, (float)(fit.X - monitor.Bounds.X), (float)(fit.Y - monitor.Bounds.Y),
                            (float)fit.Width, (float)fit.Height);
                    }
                }
            }
            _texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource
            });
            try
            {
                var data = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
                try { context.UpdateSubresource(_texture, 0, null, data.Scan0, (uint)data.Stride, 0); }
                finally { bitmap.UnlockBits(data); }
                Source = device.CreateShaderResourceView(_texture);
            }
            catch { _texture.Dispose(); throw; }
        }
        public void Dispose() { Source.Dispose(); _texture.Dispose(); }
    }
}
