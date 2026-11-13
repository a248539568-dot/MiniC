using System.Collections.ObjectModel;
using MiniC.Models;
using MiniC.Services;
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
    private double _width = 340;
    private double _height = 380;
    private bool _isCollapsed;
    private bool _isRenaming;
    private string _editName = string.Empty;
    private string _theme = "White";
    private string _material = "Crystal";
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
            OnPropertyChanged(nameof(CountBackground));
            OnPropertyChanged(nameof(CountForeground));
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
            if (!SetField(ref _material, GroupMaterialBrushes.Normalize(value))) return;
            NotifyGlassChanged();
        }
    }

    public double Opacity
    {
        get => IsSolid ? 1 : _opacity;
        set
        {
            var normalized = BackgroundTransparency.NormalizeOpacity(value);
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
    private MaterialPreferences _appearance = new();
    public MaterialPreferences Appearance
    {
        get => _appearance;
        set
        {
            if (!SetField(ref _appearance, value.NormalizeFor(Material))) return;
            OnPropertyChanged(nameof(Corners));
            OnPropertyChanged(nameof(HeaderCorners));
            OnPropertyChanged(nameof(UsesWallpaper));
            OnPropertyChanged(nameof(IsSeeThrough));
            OnPropertyChanged(nameof(IsFrosted));
            OnPropertyChanged(nameof(IsFrostedSeeThrough));
            OnPropertyChanged(nameof(IsCrystalSeeThrough));
            OnPropertyChanged(nameof(GlassBackground));
            OnPropertyChanged(nameof(HeaderBackground));
        }
    }
    public System.Windows.CornerRadius Corners => new(Appearance.Radius);
    public System.Windows.CornerRadius HeaderCorners => new(Appearance.Radius, Appearance.Radius, 0, 0);
    // The source preference is separate from material and foreground styling.
    public bool UsesWallpaper => !IsSolid && !IsSeeThrough;
    public bool IsSeeThrough => !IsSolid && Appearance.ThroughFor(Material);
    private bool _composedBackdropActive;
    public bool ComposedBackdropActive
    {
        get => _composedBackdropActive;
        internal set => SetField(ref _composedBackdropActive, value);
    }
    public bool IsFrostedSeeThrough => IsFrosted && IsSeeThrough;
    public bool IsCrystalSeeThrough => IsCrystal && IsSeeThrough;
    public bool IsCrystal => Material == "Crystal";
    public bool IsFrosted => Material == "Frosted";
    public bool IsSolid => Material == "Solid";
    public MediaBrush HeaderBackground => System.Windows.Media.Brushes.Transparent;
    public System.Windows.Media.Effects.Effect ContentShadow => GroupMaterialBrushes.ContentShadow;
    public MediaBrush PrimaryForeground => ThemeBrush("primary");
    public MediaBrush SecondaryForeground => ThemeBrush("secondary");
    public MediaBrush CountBackground => ThemeBrush("countBackground");
    public MediaBrush CountForeground => ThemeBrush("countForeground");
    public MediaBrush ItemHoverBackground => ThemeBrush("hover");
    public MediaBrush ItemSelectedBackground => ThemeBrush("selected");
    public MediaBrush ItemSelectedBorder => ThemeBrush("selectedBorder");
    public MediaBrush GlassMenuBackground => ThemeBrush("menu");
    public MediaBrush GlassMenuBorder => ThemeBrush("menuBorder");
    public MediaBrush GlassMenuHover => ThemeBrush("menuHover");
    private void NotifyGlassChanged()
    {
        OnPropertyChanged(nameof(IsCrystal));
        OnPropertyChanged(nameof(IsFrosted));
        OnPropertyChanged(nameof(IsSolid));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(IsFrostedSeeThrough));
        OnPropertyChanged(nameof(IsCrystalSeeThrough));
        OnPropertyChanged(nameof(UsesWallpaper));
        OnPropertyChanged(nameof(IsSeeThrough));
        OnPropertyChanged(nameof(PrimaryForeground));
        OnPropertyChanged(nameof(SecondaryForeground));
        OnPropertyChanged(nameof(CountBackground));
        OnPropertyChanged(nameof(CountForeground));
        OnPropertyChanged(nameof(GlassBackground));
        OnPropertyChanged(nameof(HeaderBackground));
        OnPropertyChanged(nameof(GlassMenuBackground));
        OnPropertyChanged(nameof(GlassMenuBorder));
        OnPropertyChanged(nameof(GlassMenuHover));
    }

    private MediaBrush ThemeBrush(string role)
    {
        if (role == "background") return GroupMaterialBrushes.Background(Theme, Material, Opacity);
        // The shared drop shadow protects glyphs on both bright and dark glass backgrounds.
        var color = (Theme, role) switch
        {
            (_, "primary" or "countForeground") => "#FFFFFFFF",
            (_, "secondary") => "#E6FFFFFF",
            (_, "countBackground") => "#B8242B38",
            (_, "menu") => "#F0202430",
            (_, "menuBorder") => "#50FFFFFF",
            (_, "menuHover") => "#38FFFFFF",
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

}
