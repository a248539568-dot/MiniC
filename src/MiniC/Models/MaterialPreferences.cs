namespace MiniC.Models;

/// <summary>可持久化的材质参数；透明度按材质独立保存。</summary>
public sealed record MaterialPreferences
{
    public const double DefaultRadius = 16;
    public double Radius { get; init; } = DefaultRadius;
    public double FrostedOpacity { get; init; } = 0.62;
    public double CrystalOpacity { get; init; } = 0.62;
    public bool? SeeThroughWindows { get; init; }
    public bool HideFromCapture { get; init; }
    // Legacy fields are read once using the selected material, then cleared.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FrostedSeeThrough { get; init; }
    public double FrostedBlurRadius { get; init; } = 24;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CrystalSeeThrough { get; init; }
    public double RefractionStrength { get; init; } = 1;
    public double RefractionAngle { get; init; }
    public double CrystalRippleStrength { get; init; } = 0.35;
    public double CrystalRippleScale { get; init; } = 1;
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    public bool ThroughFor(string material) => SeeThroughWindows ?? (material switch
    {
        "Frosted" => FrostedSeeThrough,
        "Crystal" => CrystalSeeThrough,
        _ => false
    });
    public MaterialPreferences NormalizeFor(string material) => Normalize() with
    {
        SeeThroughWindows = ThroughFor(material), FrostedSeeThrough = false, CrystalSeeThrough = false
    };
    public MaterialPreferences Normalize() => this with
    {
        Radius = Clamp(Radius, 0, 32, DefaultRadius),
        FrostedOpacity = Clamp(FrostedOpacity, 0, 1, 0.62),
        CrystalOpacity = Clamp(CrystalOpacity, 0, 1, 0.62),
        FrostedBlurRadius = Clamp(FrostedBlurRadius, 0, 40, 24),
        RefractionStrength = Clamp(RefractionStrength, 0, 2, 1),
        RefractionAngle = Clamp(RefractionAngle, -180, 180, 0),
        CrystalRippleStrength = Clamp(CrystalRippleStrength, 0, 1.5, 0.35),
        CrystalRippleScale = Clamp(CrystalRippleScale, 0.25, 3, 1)
    };
    public double OpacityFor(string material) => material switch
    {
        "Crystal" => CrystalOpacity, "Frosted" => FrostedOpacity, _ => 1
    };
    public MaterialPreferences WithOpacity(string material, double opacity) => (material switch
    {
        "Crystal" => this with { CrystalOpacity = opacity },
        "Frosted" => this with { FrostedOpacity = opacity },
        _ => this
    }).Normalize();
}
