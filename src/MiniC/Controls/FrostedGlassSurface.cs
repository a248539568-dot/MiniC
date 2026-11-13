using System.Windows;

namespace MiniC.Controls;

/// <summary>Diffused backdrop with a thin haze, soft bevel and a final round mask.</summary>
public sealed class FrostedGlassSurface : GlassMaterialSurface
{
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(FrostedGlassSurface), new PropertyMetadata(24d, SettingsChanged));
    public static readonly DependencyProperty NativeBackdropActiveProperty = DependencyProperty.Register(
        nameof(NativeBackdropActive), typeof(bool), typeof(FrostedGlassSurface), new PropertyMetadata(false, SettingsChanged));

    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public bool NativeBackdropActive { get => (bool)GetValue(NativeBackdropActiveProperty); set => SetValue(NativeBackdropActiveProperty, value); }

    public FrostedGlassSurface() => UpdateMaterial();

    internal FrostedGlassSurface(Func<Task<MiniC.Services.WallpaperScene>> loadScene)
        : base(new WallpaperGlassSurface(loadScene)) => UpdateMaterial();

    protected override void ConfigureSource()
    {
        Source.RefractionEnabled = false;
        Source.BlurRadius = Math.Clamp(double.IsFinite(BlurRadius) ? BlurRadius : 24, 0, 40);
        Source.Visibility = NativeBackdropActive ? Visibility.Hidden : Visibility.Visible;
        Frame.Frosted = true;
    }

    internal override GlassMaterialSurface CreatePreview() =>
        CopyCommonSettings(new FrostedGlassSurface { BlurRadius = BlurRadius });
}
