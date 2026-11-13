using MiniC.Models;

namespace MiniC.Services;

/// <summary>桌面窗口外观的唯一业务通道。窗口只提交外观状态，具体字段更新和材质参数归一化在这里完成。</summary>
internal static class WindowAppearanceService
{
    public sealed record State(string Theme, string Material, double Opacity, MaterialPreferences Appearance);

    public static State Normalize(State state)
    {
        var material = GroupMaterialBrushes.Normalize(state.Material);
        var appearance = state.Appearance.NormalizeFor(material);
        return state with { Theme = string.IsNullOrWhiteSpace(state.Theme) ? "White" : state.Theme,
            Material = material, Opacity = appearance.OpacityFor(material), Appearance = appearance };
    }

    public static State SetTheme(State state, string theme) => Normalize(state with { Theme = theme });
    public static State SetMaterial(State state, string material) => Normalize(Normalize(state) with { Material = material });
    public static State SetOpacity(State state, double opacity) =>
        Normalize(state with { Appearance = state.Appearance.WithOpacity(state.Material, opacity) });

    public static State SetThrough(State state, bool enabled) =>
        state.Material == "Solid" ? Normalize(state)
            : Normalize(state with { Appearance = state.Appearance with { SeeThroughWindows = enabled } });

    public static State SetHideFromCapture(State state, bool hidden) =>
        Normalize(state with { Appearance = state.Appearance with { HideFromCapture = hidden } });

    public static State SetAppearance(State state, MaterialPreferences appearance) => Normalize(state with { Appearance = appearance });
}
