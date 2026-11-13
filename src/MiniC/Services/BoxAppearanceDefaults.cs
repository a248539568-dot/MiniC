using MiniC.Models;
using MiniC.Widgets;

namespace MiniC.Services;

/// <summary>新建和一次性初始化收纳盒宿主时使用的基础外观。</summary>
internal static class BoxAppearanceDefaults
{
    public const int InitializationVersion = 2;
    public const string Theme = "White";
    public const string Material = "Crystal";
    public const double Opacity = 0.62;

    public static MaterialPreferences CreateAppearance() => new MaterialPreferences().NormalizeFor(Material);

    public static WindowAppearanceService.State CreateState() =>
        WindowAppearanceService.Normalize(new WindowAppearanceService.State(Theme, Material, Opacity, CreateAppearance()));

    public static bool Initialize(LayoutState layout, IEnumerable<WidgetState> widgets, Action<WidgetState> persistWidget)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(widgets);
        ArgumentNullException.ThrowIfNull(persistWidget);
        if (layout.AppearanceInitializationVersion >= InitializationVersion) return false;

        // Existing styles only need the new geometry; do not reset material settings.
        if (layout.AppearanceInitializationVersion >= 1)
        {
            if (layout.BoxHosts is not null)
                foreach (var host in layout.BoxHosts)
                    if (host.Appearance is { } appearance)
                        host.Appearance = appearance with { Radius = MaterialPreferences.DefaultRadius };
            foreach (var widget in widgets)
            {
                widget.Appearance = widget.Appearance with { Radius = MaterialPreferences.DefaultRadius };
                persistWidget(widget);
            }
            layout.AppearanceInitializationVersion = InitializationVersion;
            return true;
        }

        var initial = CreateState();
        foreach (var group in layout.Groups)
        {
            group.Theme = initial.Theme;
            group.Material = initial.Material;
            group.Opacity = initial.Opacity;
        }

        if (layout.BoxHosts is not null)
        {
            foreach (var host in layout.BoxHosts)
            {
                host.Theme = initial.Theme;
                host.Material = initial.Material;
                host.Opacity = initial.Opacity;
                host.Appearance = initial.Appearance;
            }
        }

        foreach (var widget in widgets)
        {
            widget.Theme = initial.Theme;
            widget.Material = initial.Material;
            widget.Opacity = initial.Opacity;
            widget.Appearance = initial.Appearance;
            persistWidget(widget);
        }

        layout.AppearanceInitializationVersion = InitializationVersion;
        return true;
    }
}
