using System.Windows.Media;

namespace MiniC.ViewModels;

/// <summary>收纳盒内单个真实文件、文件夹或快捷方式的界面状态。</summary>
public sealed class DesktopItem : ObservableObject
{
    private string _groupId = string.Empty;
    private string _path = string.Empty;
    private ImageSource? _icon;
    private bool _isSelected;
    private bool _isDropTarget;
    private bool _isRenaming;
    private bool _showFullName;
    private ClipboardTransferState _clipboardTransferState;
    private string _editName = string.Empty;
    private string _name = string.Empty;
    private double _x;
    private double _y;
    private double _availableHeight = double.PositiveInfinity;

    public required string Path { get => _path; set => SetField(ref _path, value); }
    public required string Name { get => _name; set => SetField(ref _name, value); }
    public bool IsDirectory { get; init; }
    public long LastWriteTicks { get; set; }
    internal bool NeedsIconRefresh { get; set; }
    public bool IsVirtual { get; init; }

    public string EditName { get => _editName; set => SetField(ref _editName, value); }
    public bool IsRenaming { get => _isRenaming; set => SetField(ref _isRenaming, value); }

    public double X { get => _x; set => SetField(ref _x, value); }
    public double Y { get => _y; set => SetField(ref _y, value); }
    public double AvailableHeight { get => _availableHeight; set => SetField(ref _availableHeight, value); }

    public string GroupId
    {
        get => _groupId;
        set => SetField(ref _groupId, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetField(ref _isSelected, value)) return;
            if (!value) ShowFullName = false;
        }
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetField(ref _isDropTarget, value);
    }

    public bool ShowFullName
    {
        get => _showFullName;
        set => SetField(ref _showFullName, value);
    }

    public ClipboardTransferState ClipboardTransferState
    {
        get => _clipboardTransferState;
        set
        {
            if (!SetField(ref _clipboardTransferState, value)) return;
            OnPropertyChanged(nameof(IsCopiedToClipboard));
            OnPropertyChanged(nameof(IsCutToClipboard));
        }
    }

    public bool IsCopiedToClipboard => ClipboardTransferState == ClipboardTransferState.Copied;
    public bool IsCutToClipboard => ClipboardTransferState == ClipboardTransferState.Cut;
}

public enum ClipboardTransferState
{
    None,
    Copied,
    Cut
}
