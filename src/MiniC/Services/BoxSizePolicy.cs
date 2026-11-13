using MiniC.Models;

namespace MiniC.Services;

/// <summary>统一新建盒子的尺寸来源，避免收纳盒和组件各自维护默认尺寸。</summary>
public static class BoxSizePolicy
{
    public const double DefaultWidth = 340;
    public const double DefaultHeight = 380;

    /// <summary>
    /// 返回新建盒子应使用的尺寸。已存在盒子时以第一个有效宿主为准，
    /// 没有可用宿主时使用统一的小盒子基准尺寸。
    /// </summary>
    public static BoxSize GetNewBoxSize(IEnumerable<BoxHostState> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        var first = hosts.FirstOrDefault(host => host is not null && IsValid(host.Width, host.Height));
        return first is null
            ? new BoxSize(DefaultWidth, DefaultHeight)
            : new BoxSize(first.Width, first.Height);
    }

    private static bool IsValid(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}

public readonly record struct BoxSize(double Width, double Height);
