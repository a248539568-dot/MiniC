using MiniC.Widgets;

namespace MiniC.Services;

internal sealed record BoxCreationOption(string Name, WidgetKind? WidgetKind,
    uint CommandId, ShellContextMenuResult Result)
{
    internal string MenuLabel => "新建" + Name;
}

/// <summary>Shared creation entries for the desktop Shell menu and the tray.</summary>
internal static class BoxCreationCatalog
{
    internal static IReadOnlyList<BoxCreationOption> Options { get; } = Array.AsReadOnly(new[]
    {
        new BoxCreationOption("收纳盒", null, 0x7001, ShellContextMenuResult.CreateGroupRequested),
        new BoxCreationOption("天气", WidgetKind.Weather, 0x7004, ShellContextMenuResult.CreateWeatherRequested),
        new BoxCreationOption("日程中心", WidgetKind.Planner, 0x7005, ShellContextMenuResult.CreatePlannerRequested)
    });

    internal static BoxCreationOption? Find(ShellContextMenuResult result) =>
        Options.FirstOrDefault(option => option.Result == result);

    internal static BoxCreationOption? Find(uint commandId) =>
        Options.FirstOrDefault(option => option.CommandId == commandId);
}
