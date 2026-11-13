using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MiniC.Effects;

public sealed class CrystalGlassEffect : ShaderEffect
{
    internal static double LensWidthFor(double width, double height) => Math.Min(32, Math.Min(width, height) * 0.4);
    private static readonly PixelShader Shader = LoadShader();
    private static PixelShader LoadShader()
    {
        var shader = new PixelShader { UriSource = new Uri("pack://application:,,,/MiniC;component/Effects/CrystalGlass.ps", UriKind.Absolute) };
        shader.Freeze(); return shader;
    }
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        nameof(Input), typeof(CrystalGlassEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty DimensionsProperty = DependencyProperty.Register(nameof(Dimensions),
        typeof(System.Windows.Point), typeof(CrystalGlassEffect), new UIPropertyMetadata(new System.Windows.Point(380,320), DimensionsChanged));
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength),
        typeof(double), typeof(CrystalGlassEffect), new UIPropertyMetadata(1d, WaveSettingsChanged));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius),
        typeof(double), typeof(CrystalGlassEffect), new UIPropertyMetadata(Controls.GroupGeometry.Radius, PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(nameof(Direction),
        typeof(System.Windows.Point), typeof(CrystalGlassEffect), new UIPropertyMetadata(new System.Windows.Point(1, 0), PixelShaderConstantCallback(3)));
    public static readonly DependencyProperty RippleStrengthProperty = DependencyProperty.Register(nameof(RippleStrength),
        typeof(double), typeof(CrystalGlassEffect), new UIPropertyMetadata(0.35d, WaveSettingsChanged));
    public static readonly DependencyProperty RippleScaleProperty = DependencyProperty.Register(nameof(RippleScale),
        typeof(double), typeof(CrystalGlassEffect), new UIPropertyMetadata(1d, WaveSettingsChanged));
    public static readonly DependencyProperty LensWidthProperty = DependencyProperty.Register(nameof(LensWidth),
        typeof(double), typeof(CrystalGlassEffect), new UIPropertyMetadata(32d, PixelShaderConstantCallback(6)));
    public static readonly DependencyProperty BevelExtensionProperty = DependencyProperty.Register(nameof(BevelExtension),
        typeof(double), typeof(CrystalGlassEffect), new UIPropertyMetadata(24d, PixelShaderConstantCallback(7)));
    private static readonly DependencyProperty SamplingProperty = DependencyProperty.Register("Sampling",
        typeof(System.Windows.Point), typeof(CrystalGlassEffect),
        new UIPropertyMetadata(new System.Windows.Point(1d / 380, 1d / 320), PixelShaderConstantCallback(8)));
    private static readonly DependencyProperty PhaseXProperty = DependencyProperty.Register("PhaseX",
        typeof(Point3D), typeof(CrystalGlassEffect), new UIPropertyMetadata(new Point3D(2.28, 2.88, 0.17), PixelShaderConstantCallback(9)));
    private static readonly DependencyProperty PhaseYProperty = DependencyProperty.Register("PhaseY",
        typeof(Point3D), typeof(CrystalGlassEffect), new UIPropertyMetadata(new Point3D(-3.04, 1.6, 0.41), PixelShaderConstantCallback(10)));
    private static readonly DependencyProperty WaveSamplingProperty = DependencyProperty.Register("WaveSampling",
        typeof(System.Windows.Point), typeof(CrystalGlassEffect), new UIPropertyMetadata(new System.Windows.Point(8.4 / 380, 8.4 / 320), PixelShaderConstantCallback(11)));

    private static void WaveSettingsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        => ((CrystalGlassEffect)target).UpdateSampling();

    private static void DimensionsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        PixelShaderConstantCallback(0)(target, args);
        ((CrystalGlassEffect)target).UpdateSampling();
    }

    private void UpdateSampling()
    {
        var width = Math.Max(1, Dimensions.X);
        var height = Math.Max(1, Dimensions.Y);
        SetValue(SamplingProperty, new System.Windows.Point(Strength / width, Strength / height));
        // Precompute uniform wave coefficients to stay within WPF ps_2_0's ALU budget.
        SetValue(PhaseXProperty, new Point3D(width * 0.006 * RippleScale, height * 0.009 * RippleScale, 0.17));
        SetValue(PhaseYProperty, new Point3D(width * -0.008 * RippleScale, height * 0.005 * RippleScale, 0.41));
        SetValue(WaveSamplingProperty, new System.Windows.Point(Strength * RippleStrength * 24 / width, Strength * RippleStrength * 24 / height));
    }
    public System.Windows.Media.Brush Input { get => (System.Windows.Media.Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public System.Windows.Point Dimensions { get => (System.Windows.Point)GetValue(DimensionsProperty); set => SetValue(DimensionsProperty, value); }
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public System.Windows.Point Direction { get => (System.Windows.Point)GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }
    public double RippleStrength { get => (double)GetValue(RippleStrengthProperty); set => SetValue(RippleStrengthProperty, value); }
    public double RippleScale { get => (double)GetValue(RippleScaleProperty); set => SetValue(RippleScaleProperty, value); }
    public double LensWidth { get => (double)GetValue(LensWidthProperty); set => SetValue(LensWidthProperty, value); }
    public double BevelExtension { get => (double)GetValue(BevelExtensionProperty); set => SetValue(BevelExtensionProperty, value); }
    public CrystalGlassEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(DimensionsProperty); UpdateShaderValue(StrengthProperty);
        UpdateShaderValue(CornerRadiusProperty); UpdateShaderValue(DirectionProperty);
        UpdateShaderValue(RippleStrengthProperty); UpdateShaderValue(RippleScaleProperty);
        UpdateShaderValue(LensWidthProperty);
        UpdateShaderValue(BevelExtensionProperty);
        UpdateShaderValue(SamplingProperty);
        UpdateShaderValue(PhaseXProperty); UpdateShaderValue(PhaseYProperty); UpdateShaderValue(WaveSamplingProperty);
    }
}
