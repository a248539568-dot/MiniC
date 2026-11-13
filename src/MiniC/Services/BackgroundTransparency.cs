namespace MiniC.Services;

/// <summary>界面显示透光程度，布局继续存储底色不透明度，兼容旧配置。</summary>
internal static class BackgroundTransparency
{
    public static double NormalizeOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.62;
    public static double ToTransparency(double opacity) => 1 - NormalizeOpacity(opacity);
    public static double ToOpacity(double transparency) => double.IsFinite(transparency)
        ? 1 - Math.Clamp(transparency, 0, 1) : 0.62;
}
