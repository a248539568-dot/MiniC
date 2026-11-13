using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MiniC.Services;

/// <summary>三种收纳盒材质的局部色层；模糊背景由各自的背景层绘制。</summary>
internal static class GroupMaterialBrushes
{
    public static IReadOnlyList<(string Id, string Name)> Choices { get; } =
        Array.AsReadOnly(new[] { ("Crystal", "折射玻璃"), ("Frosted", "液态玻璃"), ("Solid", "纯色") });

    public static string Normalize(string? material) => material is "Crystal" or "Frosted" ? material : "Solid";

    public static System.Windows.Media.Effects.Effect ContentShadow { get; } = CreateContentShadow();
    private static System.Windows.Media.Effects.Effect CreateContentShadow()
    {
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            // Keep white foreground content legible on bright backgrounds with a
            // soft, low-density drop shadow rather than a tight outline.
            Color = Color.FromRgb(0, 0, 0),
            BlurRadius = 5,
            ShadowDepth = 1,
            Direction = 270,
            Opacity = 0.60,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
        };
        shadow.Freeze();
        return shadow;
    }

    public static Brush Background(string theme, string material, double opacity)
    {
        opacity = BackgroundTransparency.NormalizeOpacity(opacity);
        var color = ThemeColor(theme);
        material = Normalize(material);
        var alpha = material == "Frosted" ? 0.18 * opacity
            : material == "Crystal" ? 0.09 * opacity : 1;
        // Layered HWNDs pass mouse input through alpha-zero pixels, even when
        // Acrylic is visibly rendered behind them. Keep a continuous input surface.
        color.A = (byte)Math.Clamp(Math.Round(255 * alpha), 1, 255);
        return Frozen(new SolidColorBrush(color));
    }
    internal static Color ThemeColor(string theme) => theme switch
    {
        "Blue" => Color.FromRgb(129, 181, 244), "Mint" => Color.FromRgb(116, 211, 179),
        "Purple" => Color.FromRgb(183, 153, 236), "Amber" => Color.FromRgb(245, 192, 117),
        "Graphite" => Color.FromRgb(27, 30, 41), _ => Color.FromRgb(247, 250, 255)
    };

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

}



