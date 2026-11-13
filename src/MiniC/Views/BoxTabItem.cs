using System.ComponentModel;
using System.Runtime.CompilerServices;
using MiniC.Models;

namespace MiniC.Views;

public sealed class BoxTabItem(BoxTabRef tab, string title) : INotifyPropertyChanged
{
    private bool _isActive;
    public BoxTabRef Tab { get; } = tab;
    public string Title { get; } = title;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
