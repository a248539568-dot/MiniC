using System.Windows;
using System.Windows.Media;
using MiniC.Effects;
using MiniC.Services;
using Point = System.Windows.Point;

namespace MiniC.Controls;

/// <summary>壁纸空间中的玻璃背景，随窗口移动重新采样；着色器只作用于此背景层。</summary>
public sealed class WallpaperGlassSurface : FrameworkElement
{
    public static readonly DependencyProperty UseWallpaperProperty = DependencyProperty.Register(nameof(UseWallpaper), typeof(bool), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(double), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(GroupGeometry.Radius, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength), typeof(double), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(nameof(BlurRadius), typeof(double), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public static readonly DependencyProperty RefractionEnabledProperty = DependencyProperty.Register(nameof(RefractionEnabled), typeof(bool), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public static readonly DependencyProperty RippleStrengthProperty = DependencyProperty.Register(nameof(RippleStrength), typeof(double), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(0.35d, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public static readonly DependencyProperty RippleScaleProperty = DependencyProperty.Register(nameof(RippleScale), typeof(double), typeof(WallpaperGlassSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, SettingsChanged));
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public bool RefractionEnabled { get => (bool)GetValue(RefractionEnabledProperty); set => SetValue(RefractionEnabledProperty, value); }
    public double RippleStrength { get => (double)GetValue(RippleStrengthProperty); set => SetValue(RippleStrengthProperty, value); }
    public double RippleScale { get => (double)GetValue(RippleScaleProperty); set => SetValue(RippleScaleProperty, value); }
    public bool UseWallpaper { get => (bool)GetValue(UseWallpaperProperty); set => SetValue(UseWallpaperProperty, value); }
    private static void SettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (WallpaperGlassSurface)d;
        if (e.Property == UseWallpaperProperty)
        {
            surface._generation++;
            surface._scene = null;
            if (surface.UseWallpaper) _ = surface.RefreshAsync();
        }
        surface.UpdateDimensions();
    }
    private WallpaperScene? _scene;
    private Window? _owner;
    private CrystalGlassEffect? _glass;
    private int _generation;
    private readonly Func<Task<WallpaperScene>> _loadScene;
    public WallpaperGlassSurface() : this(WallpaperTextureService.GetAsync) { }
    internal WallpaperGlassSurface(Func<Task<WallpaperScene>> loadScene)
    {
        _loadScene = loadScene;
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner is not null) _owner.LocationChanged += OwnerMoved;
            _ = RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _generation++;
            if (_owner is not null) _owner.LocationChanged -= OwnerMoved;
            _owner = null; _scene = null;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = RefreshAsync(); };
        SizeChanged += (_, _) => UpdateDimensions();
    }

    public async Task RefreshAsync()
    {
        if (!IsLoaded || !UseWallpaper) return;
        var generation = ++_generation;
        var version = WallpaperTextureService.Version;
        WallpaperScene scene;
        try { scene = await _loadScene(); }
        catch (Exception error)
        {
            MiniCLogger.Error(nameof(WallpaperGlassSurface), error, "Wallpaper scene refresh failed.");
            return;
        }
        if (generation != _generation || version != WallpaperTextureService.Version || !IsLoaded || !UseWallpaper) return;
        _scene = scene;
        UpdateDimensions();
        InvalidateVisual();
    }

    private void UpdateDimensions()
    {
        // Settings are live: changing the blur slider must update the effect,
        // not wait for a wallpaper reload. The crystal shader stays separate.
        if (!UseWallpaper) Effect = null;
        else if (BlurRadius > 0)
        {
            if (Effect is not System.Windows.Media.Effects.BlurEffect blur || blur.Radius != BlurRadius)
                Effect = new System.Windows.Media.Effects.BlurEffect
                { Radius = BlurRadius, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        }
        else if (RefractionEnabled && RenderCapability.IsPixelShaderVersionSupported(2, 0))
        {
            _glass ??= new CrystalGlassEffect();
            if (Effect != _glass) Effect = _glass;
        }
        else Effect = null;
        // Keep the input rectangular and local; clipping its corners before
        // refraction would sample transparent pixels along the curved lens.
        var inputRadius = Effect == _glass && _glass is not null ? 0 : Radius;
        // Blur must receive opaque pixels beyond the visible edge. Clipping the
        // input first mixes transparency into the convolution at the rim.
        Clip = BlurRadius > 0 ? null : new RectangleGeometry(
            new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), inputRadius, inputRadius);
        if (_glass is not null && BlurRadius <= 0)
        {
            _glass.Dimensions = new Point(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            _glass.CornerRadius = Math.Clamp(Radius, 0,
                Math.Min(_glass.Dimensions.X, _glass.Dimensions.Y) / 2);
            _glass.Strength = Strength;
            _glass.LensWidth = CrystalGlassEffect.LensWidthFor(_glass.Dimensions.X, _glass.Dimensions.Y);
            _glass.BevelExtension = Math.Max(0, _glass.LensWidth - _glass.CornerRadius);
            _glass.RippleStrength = RippleStrength;
            _glass.RippleScale = RippleScale;
            var radians = Angle * Math.PI / 180;
            _glass.Direction = new Point(Math.Cos(radians), Math.Sin(radians));
        }
        InvalidateVisual();
    }
    private void OwnerMoved(object? sender, EventArgs e)
    {
        if (UseWallpaper && IsVisible) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Live transparency is the baseline. A cached desktop image must never
        // cover actual rear windows, including when capture is unavailable.
        if (!UseWallpaper) return;
        if (_scene is null || _scene.Monitors.Count == 0 || !IsLoaded)
            return;
        var origin = PointToScreen(new Point());
        var right = PointToScreen(new Point(Math.Max(1, ActualWidth), 0));
        var bottom = PointToScreen(new Point(0, Math.Max(1, ActualHeight)));
        // 包括预览的 RenderTransform 缩放：纹理仍与物理屏幕上的壁纸对齐。
        var scaleX = Math.Max(0.01, (right.X - origin.X) / Math.Max(1, ActualWidth));
        var scaleY = Math.Max(0.01, (bottom.Y - origin.Y) / Math.Max(1, ActualHeight));
        var padding = Math.Clamp(double.IsFinite(BlurRadius) ? BlurRadius : 0, 0, 40);
        var local = new Rect(-padding, -padding, ActualWidth + padding * 2, ActualHeight + padding * 2);
        var refracting = Effect == _glass && _glass is not null;
        var inputRadius = refracting || padding > 0 ? 0 : Radius;
        dc.PushClip(new RectangleGeometry(local, inputRadius, inputRadius));
        dc.DrawRectangle(new SolidColorBrush(_scene.Background), null, local);
        dc.PushTransform(new ScaleTransform(1 / scaleX, 1 / scaleY));
        dc.PushTransform(new TranslateTransform(-origin.X, -origin.Y));
        var virtualBounds = _scene.Monitors.Select(monitor => monitor.Bounds).Aggregate(Rect.Union);
        foreach (var monitor in _scene.Monitors)
        {
            if (monitor.Image is not { } image) continue;
            dc.PushClip(new RectangleGeometry(monitor.Bounds));
            if (_scene.Position == 1)
            {
                var brush = new ImageBrush(image) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(monitor.Bounds.TopLeft, new System.Windows.Size(image.PixelWidth, image.PixelHeight)), Stretch = Stretch.Fill };
                dc.DrawRectangle(brush, null, monitor.Bounds);
            }
            else dc.DrawImage(image, WallpaperTextureService.FitImage(_scene.Position == 5 ? virtualBounds : monitor.Bounds,
                image.PixelWidth, image.PixelHeight, _scene.Position));
            dc.Pop();
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }

}


