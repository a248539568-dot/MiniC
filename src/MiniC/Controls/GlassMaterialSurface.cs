using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MiniC.Controls;

/// <summary>Shared background, tint, bevel and final mask for glass materials.</summary>
public abstract class GlassMaterialSurface : Grid
{
    public static readonly DependencyProperty UseWallpaperProperty = DependencyProperty.Register(
        nameof(UseWallpaper), typeof(bool), typeof(GlassMaterialSurface), new PropertyMetadata(false, SettingsChanged));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(GlassMaterialSurface), new PropertyMetadata(Brushes.Transparent, SettingsChanged));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(GlassMaterialSurface), new PropertyMetadata(GroupGeometry.Radius, SettingsChanged));

    protected WallpaperGlassSurface Source { get; }
    protected GlassSurfaceFrame Frame { get; } = new();
    private readonly Border _tint = new() { IsHitTestVisible = false };
    private (double Width, double Height, double Radius)? _maskBounds;

    public bool UseWallpaper { get => (bool)GetValue(UseWallpaperProperty); set => SetValue(UseWallpaperProperty, value); }
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    protected GlassMaterialSurface() : this(new WallpaperGlassSurface()) { }

    private protected GlassMaterialSurface(WallpaperGlassSurface source)
    {
        Source = source;
        Source.RefractionEnabled = false;
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(Source);
        Children.Add(_tint);
        Children.Add(Frame);
    }

    public Task RefreshAsync() => Source.RefreshAsync();
    public void InvalidateBackdrop()
    {
        if (UseWallpaper && IsVisible) Source.InvalidateVisual();
    }
    internal abstract GlassMaterialSurface CreatePreview();
    protected abstract void ConfigureSource();
    protected virtual bool SupportsWallpaper => true;
    protected virtual Brush ResolveTint() => Tint ?? Brushes.Transparent;

    protected T CopyCommonSettings<T>(T target) where T : GlassMaterialSurface
    {
        target.Radius = Radius;
        target.Tint = Tint;
        target.UseWallpaper = UseWallpaper;
        return target;
    }

    protected static void SettingsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        ((GlassMaterialSurface)target).UpdateMaterial();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateMaterial();
    }

    protected void UpdateMaterial()
    {
        var radius = Math.Clamp(double.IsFinite(Radius) ? Radius : GroupGeometry.Radius, 0,
            Math.Min(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)) / 2);
        var bounds = (Math.Max(1, ActualWidth), Math.Max(1, ActualHeight), radius);
        if (_maskBounds != bounds)
        {
            _maskBounds = bounds;
            var shape = new RectangleGeometry(new Rect(0, 0, bounds.Item1, bounds.Item2), radius, radius);
            // A padded blur extends the children's visual bounds. Keep both the
            // final clip and mask anchored to the material's layout rectangle.
            shape.Freeze();
            Clip = shape;
            var mask = new DrawingBrush(new GeometryDrawing(Brushes.White, null, shape)) { Stretch = Stretch.Fill };
            mask.Freeze();
            OpacityMask = mask;
        }
        Source.UseWallpaper = SupportsWallpaper && UseWallpaper;
        Source.Radius = radius;
        _tint.Background = ResolveTint();
        _tint.CornerRadius = new CornerRadius(radius);
        Frame.Radius = radius;
        ConfigureSource();
    }
}
