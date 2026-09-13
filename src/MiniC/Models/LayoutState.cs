namespace MiniC.Models;

/// <summary>应用布局的持久化根对象，仅保存可序列化状态，不包含界面行为。</summary>
public sealed class LayoutState
{
    public List<GroupState> Groups { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("assignments")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LegacyAssignments { get; set; }
    public bool? StartWithWindows { get; set; }
    public string? DefaultTheme { get; set; }
    public string? DefaultMaterial { get; set; }
    public double? DefaultOpacity { get; set; }
    public NativeDesktopState NativeDesktop { get; set; } = new();
}

/// <summary>桌面显示层的持久化状态；项目始终保留在真实桌面目录。</summary>
public sealed class NativeDesktopState
{
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DesktopIconPositionState> ManagedPositions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>桌面显示层中的图标屏幕坐标。</summary>
public sealed class DesktopIconPositionState
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>单个收纳盒的持久化快照。</summary>
public sealed class GroupState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "收纳盒";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 320;
    public bool IsCollapsed { get; set; }
    public string Theme { get; set; } = "White";
    public string Material { get; set; } = "Clear";
    public double Opacity { get; set; } = 0.62;
    public string ViewMode { get; set; } = "Icons";
    public List<string> ItemOrder { get; set; } = [];
    public string? TabGroupId { get; set; }
    public int TabOrder { get; set; }
}
