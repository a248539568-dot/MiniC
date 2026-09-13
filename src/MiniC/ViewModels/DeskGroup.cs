using System.Collections.ObjectModel;
using MediaBrush = System.Windows.Media.Brush;

namespace MiniC.ViewModels;

/// <summary>
/// 收纳盒的界面状态模型，包含布局、主题、选择框和图标集合等可绑定属性。
/// </summary>
public sealed class DeskGroup : ObservableObject
{
    private string _name = "新收纳盒";
    private double _x = 36;
    private double _y = 104;
    private double _width = 380;
    private double _height = 320;
    private bool _isCollapsed;
    private bool _isRenaming;
    private string _editName = string.Empty;
    private string _theme = "White";
    private string _material = "Clear";
    private double _opacity = 0.62;
    private string _viewMode = "Icons";
    private bool _isActive;
    private string? _tabGroupId;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ObservableCollection<DesktopItem> Items { get; } = [];
    public List<string> ItemOrder { get; set; } = [];

    public string Name { get => _name; set => SetField(ref _name, value); }
    public double X { get => _x; set => SetField(ref _x, value); }
    public double Y { get => _y; set => SetField(ref _y, value); }
    public double Width { get => _width; set => SetField(ref _width, value); }
    public double Height { get => _height; set => SetField(ref _height, value); }
    public bool IsCollapsed { get => _isCollapsed; set => SetField(ref _isCollapsed, value); }
    public bool IsRenaming { get => _isRenaming; set => SetField(ref _isRenaming, value); }
    public string EditName { get => _editName; set => SetField(ref _editName, value); }
    public string Theme
    {
        get => _theme;
        set
        {
            if (!SetField(ref _theme, value)) return;
            OnPropertyChanged(nameof(GlassBackground));
            OnPropertyChanged(nameof(HeaderBackground));
            OnPropertyChanged(nameof(PrimaryForeground));
            OnPropertyChanged(nameof(SecondaryForeground));
            OnPropertyChanged(nameof(ItemHoverBackground));
            OnPropertyChanged(nameof(ItemSelectedBackground));
            OnPropertyChanged(nameof(ItemSelectedBorder));
            OnPropertyChanged(nameof(GlassMenuBackground));
            OnPropertyChanged(nameof(GlassMenuBorder));
            OnPropertyChanged(nameof(GlassMenuHover));
        }
    }

    public string Material
    {
        get => _material;
        set
        {
            if (!SetField(ref _material, value)) return;
            NotifyGlassChanged();
        }
    }

    public double Opacity
    {
        get => _opacity;
        set
        {
            var normalized = Math.Clamp(value, 0.22, 0.92);
            if (!SetField(ref _opacity, normalized)) return;
            NotifyGlassChanged();
        }
    }

    public string ViewMode { get => _viewMode; set => SetField(ref _viewMode, value); }
    public string? TabGroupId { get => _tabGroupId; set => SetField(ref _tabGroupId, value); }
    public int TabOrder { get; set; }
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetField(ref _isActive, value)) return;
        }
    }
    public MediaBrush GlassBackground => ThemeBrush("background");
    public MediaBrush HeaderBackground => ThemeBrush("header");
    public MediaBrush PrimaryForeground => ThemeBrush("primary");
    public MediaBrush SecondaryForeground => ThemeBrush("secondary");
    public MediaBrush ItemHoverBackground => ThemeBrush("hover");
    public MediaBrush ItemSelectedBackground => ThemeBrush("selected");
    public MediaBrush ItemSelectedBorder => ThemeBrush("selectedBorder");
    public MediaBrush GlassMenuBackground => ThemeBrush("menu");
    public MediaBrush GlassMenuBorder => ThemeBrush("menuBorder");
    public MediaBrush GlassMenuHover => ThemeBrush("menuHover");
    private void NotifyGlassChanged()
    {
        OnPropertyChanged(nameof(GlassBackground));
        OnPropertyChanged(nameof(HeaderBackground));
        OnPropertyChanged(nameof(GlassMenuBackground));
        OnPropertyChanged(nameof(GlassMenuBorder));
        OnPropertyChanged(nameof(GlassMenuHover));
    }

    private MediaBrush ThemeBrush(string role)
    {
        if (role == "background") return GlassBrush();
        var color = (Theme, role) switch
        {
            ("Graphite", "header") => Material == "Satin" ? "#42FFFFFF" : "#28FFFFFF",
            (_, "header") => Material == "Frosted" ? "#52FFFFFF" : Material == "Satin" ? "#42FFFFFF" : "#2EFFFFFF",
            ("Graphite", "menu") => "#ED20242E",
            (_, "menu") => "#EAF7FAFF",
            ("Graphite", "menuBorder") => "#70FFFFFF",
            (_, "menuBorder") => "#D8FFFFFF",
            ("Graphite", "menuHover") => "#38FFFFFF",
            (_, "menuHover") => "#486B88AC",
            ("Graphite", "primary") => "#FFF5F7FB",
            (_, "primary") => "#E5222936",
            ("Graphite", "secondary") => "#B9B7C0D2",
            (_, "secondary") => "#A85A6475",
            ("Blue", "hover") => "#386AADE0",
            ("Mint", "hover") => "#3851A98B",
            ("Purple", "hover") => "#387E6BC2",
            ("Amber", "hover") => "#38C18A42",
            ("Graphite", "hover") => "#385E82B4",
            (_, "hover") => "#386080A8",
            ("Blue", "selected") => "#8060A5D8",
            ("Mint", "selected") => "#8051A584",
            ("Purple", "selected") => "#807A68BE",
            ("Amber", "selected") => "#80BC8238",
            ("Graphite", "selected") => "#806385B5",
            (_, "selected") => "#805979A4",
            ("Blue", "selectedBorder") => "#D091C9EE",
            ("Mint", "selectedBorder") => "#D080C9AE",
            ("Purple", "selectedBorder") => "#D0AA98E2",
            ("Amber", "selectedBorder") => "#D0E0AE68",
            ("Graphite", "selectedBorder") => "#D094B2DB",
            (_, "selectedBorder") => "#D08FB0D9",
            _ => "#FFFFFFFF"
        };
        return (MediaBrush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
    }

    private MediaBrush GlassBrush()
    {
        var color = Theme switch
        {
            "Blue" => System.Windows.Media.Color.FromRgb(221, 235, 255),
            "Mint" => System.Windows.Media.Color.FromRgb(220, 247, 238),
            "Purple" => System.Windows.Media.Color.FromRgb(234, 224, 255),
            "Amber" => System.Windows.Media.Color.FromRgb(255, 240, 210),
            "Graphite" => System.Windows.Media.Color.FromRgb(27, 30, 41),
            _ => System.Windows.Media.Color.FromRgb(247, 250, 255)
        };
        var alphaScale = Material switch
        {
            "Frosted" => 1.12,
            "Crystal" => 0.76,
            "Satin" => 0.92,
            _ => 1.0
        };
        if (Material == "Frosted" && Theme != "Graphite") color = Mix(color, System.Windows.Media.Colors.White, 0.18);
        if (Material == "Satin") color = Mix(color, Theme == "Graphite" ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White, 0.08);
        var glassAlpha = 0.13 + Opacity * 0.38;
        color.A = (byte)Math.Clamp(Math.Round(255 * glassAlpha * alphaScale), 36, 148);
        return new System.Windows.Media.SolidColorBrush(color);
    }

    private static System.Windows.Media.Color Mix(System.Windows.Media.Color source, System.Windows.Media.Color target, double amount) =>
        System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(source.R + (target.R - source.R) * amount),
            (byte)Math.Round(source.G + (target.G - source.G) * amount),
            (byte)Math.Round(source.B + (target.B - source.B) * amount));
}
