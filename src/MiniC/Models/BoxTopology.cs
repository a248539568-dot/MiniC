namespace MiniC.Models;

public enum BoxContentKind { DesktopGroup, Widget }

public sealed record BoxTabRef(BoxContentKind Kind, string Id);

public sealed class BoxHostState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<BoxTabRef> Tabs { get; set; } = [];
    public BoxTabRef? ActiveTab { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsCollapsed { get; set; }
    public MaterialPreferences? Appearance { get; set; }
    public string? Theme { get; set; }
    public string? Material { get; set; }
    public double? Opacity { get; set; }
}
