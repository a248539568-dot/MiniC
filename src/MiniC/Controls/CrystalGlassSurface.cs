using System.Windows;

namespace MiniC.Controls;

/// <summary>Refractive background using the shared glass surface layers.</summary>
public sealed class CrystalGlassSurface : GlassMaterialSurface
{
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
        nameof(Strength), typeof(double), typeof(CrystalGlassSurface), new PropertyMetadata(1d, SettingsChanged));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(CrystalGlassSurface), new PropertyMetadata(0d, SettingsChanged));
    public static readonly DependencyProperty RippleStrengthProperty = DependencyProperty.Register(
        nameof(RippleStrength), typeof(double), typeof(CrystalGlassSurface), new PropertyMetadata(0.35d, SettingsChanged));
    public static readonly DependencyProperty RippleScaleProperty = DependencyProperty.Register(
        nameof(RippleScale), typeof(double), typeof(CrystalGlassSurface), new PropertyMetadata(1d, SettingsChanged));

    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public double RippleStrength { get => (double)GetValue(RippleStrengthProperty); set => SetValue(RippleStrengthProperty, value); }
    public double RippleScale { get => (double)GetValue(RippleScaleProperty); set => SetValue(RippleScaleProperty, value); }

    public CrystalGlassSurface() => UpdateMaterial();

    internal CrystalGlassSurface(Func<Task<MiniC.Services.WallpaperScene>> loadScene)
        : base(new WallpaperGlassSurface(loadScene)) => UpdateMaterial();

    protected override void ConfigureSource()
    {
        Source.RefractionEnabled = true;
        Source.Strength = Strength;
        Source.Angle = Angle;
        Source.RippleStrength = RippleStrength;
        Source.RippleScale = RippleScale;
        Frame.Strength = Strength;
        Frame.Angle = Angle;
    }

    internal override GlassMaterialSurface CreatePreview() => CopyCommonSettings(new CrystalGlassSurface
    {
        Strength = Strength, Angle = Angle, RippleStrength = RippleStrength, RippleScale = RippleScale
    });
}
