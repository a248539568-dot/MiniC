using System.Windows;
using System.Windows.Media;

namespace MiniC.Controls;

/// <summary>An opaque theme colour without background sampling or optical effects.</summary>
public sealed class SolidMaterialSurface : GlassMaterialSurface
{
    public SolidMaterialSurface() => UpdateMaterial();
    protected override bool SupportsWallpaper => false;
    protected override System.Windows.Media.Brush ResolveTint()
    {
        var color = Tint is SolidColorBrush brush ? brush.Color : Colors.White;
        color.A = 255;
        var opaque = new SolidColorBrush(color);
        opaque.Freeze();
        return opaque;
    }
    protected override void ConfigureSource()
    {
        Source.RefractionEnabled = false;
        Source.BlurRadius = 0;
        Source.Visibility = Visibility.Collapsed;
        Frame.Visibility = Visibility.Collapsed;
    }
    internal override GlassMaterialSurface CreatePreview() => CopyCommonSettings(new SolidMaterialSurface());
}
