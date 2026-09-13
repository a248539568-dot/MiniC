namespace MiniC.Services;

/// <summary>在桌面层和所有收纳盒之间共享应用内部文件拖动状态。</summary>
public sealed class DesktopDragStateService
{
    public bool IsActive { get; private set; }
    public event Action<bool>? Changed;

    public void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        Changed?.Invoke(active);
    }
}
