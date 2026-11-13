using System.Windows;

namespace MiniC.Controls;

/// <summary>真实收纳盒与预览共享的基础几何。</summary>
public static class GroupGeometry
{
    // WPF masks, material bevels and native backdrops share the same DIP radius.
    public const double Radius = Models.MaterialPreferences.DefaultRadius;
    public static CornerRadius Corners { get; } = new(Radius);
    public static CornerRadius HeaderCorners { get; } = new(Radius, Radius, 0, 0);
}
