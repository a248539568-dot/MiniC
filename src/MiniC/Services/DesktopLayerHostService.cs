using System.Runtime.InteropServices;

namespace MiniC.Services;

/// <summary>
/// 把独立收纳盒窗口放在 Explorer 桌面上方、普通应用下方。
/// </summary>
public sealed class DesktopLayerHostService
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint GwHwndNext = 2;
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExNoActivate = 0x08000000L;
    private const int RgnOr = 2;
    private const int GroupCornerRadius = 20;

    /// <summary>把主桌面窗口标记为工具窗口，确保它不出现在 Alt+Tab 和任务栏应用列表中。</summary>
    public bool ConfigureAsDesktopToolWindow(nint windowHandle, bool preventActivation = false)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle)) return false;
        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        var activationStyle = preventActivation
            ? extendedStyle | WsExNoActivate
            : extendedStyle & ~WsExNoActivate;
        SetWindowLongPtr(windowHandle, GwlExStyle,
            new nint((activationStyle | WsExToolWindow) & ~WsExAppWindow & ~WsExTransparent));
        var updatedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        return (updatedStyle & WsExToolWindow) != 0
               && (updatedStyle & WsExAppWindow) == 0
               && ((updatedStyle & WsExNoActivate) != 0) == preventActivation;
    }

    /// <summary>
    /// 限定桌面窗口真正占用的屏幕区域。传入 null 恢复完整窗口区域，空集合则让窗口完全不参与命中测试。
    /// Windows 在成功后接管合并区域句柄，调用方无需释放。
    /// </summary>
    public bool ApplyInteractiveRegions(nint windowHandle, IReadOnlyCollection<DesktopLayerBounds>? regions)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle)) return false;
        if (regions is null) return SetWindowRgn(windowHandle, 0, true) != 0;

        var combinedRegion = CreateRectRgn(0, 0, 0, 0);
        if (combinedRegion == 0) return false;
        foreach (var region in regions)
        {
            var itemRegion = CreateRoundRectRgn(region.X, region.Y,
                region.X + Math.Max(1, region.Width), region.Y + Math.Max(1, region.Height),
                GroupCornerRadius, GroupCornerRadius);
            if (itemRegion == 0) continue;
            _ = CombineRgn(combinedRegion, combinedRegion, itemRegion, RgnOr);
            _ = DeleteObject(itemRegion);
        }

        if (SetWindowRgn(windowHandle, combinedRegion, true) != 0) return true;
        _ = DeleteObject(combinedRegion);
        return false;
    }

    /// <summary>
    /// 把透明 WPF 窗口放到 Explorer 桌面正上方、所有普通应用正下方，并禁止窗口激活。
    /// 不使用跨进程 SetParent，避免 AllowsTransparency 窗口停止渲染。
    /// </summary>
    public DesktopLayerAttachResult TryPlaceAboveDesktopContent(nint windowHandle, bool preventActivation = true)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle) || !GetWindowRect(windowHandle, out var bounds))
            return new(false, 0, "桌面显示层窗口尚未创建。");

        var hostHandle = ExplorerDesktopWindowService.FindContentHost();
        if (hostHandle == 0)
            return new(false, 0, "Explorer 没有提供可用的桌面内容宿主。");

        if (GetParent(windowHandle) != 0) return new(false, 0, "收纳盒窗口不是独立顶层窗口。");
        if (!ConfigureAsDesktopToolWindow(windowHandle, preventActivation))
            return new(false, 0, "桌面显示层无法切换为非激活工具窗口。");
        if (IsPlacedAboveDesktopContent(windowHandle, hostHandle))
            return new(true, hostHandle, 0, "桌面显示层层级已正确，无需调整。");

        if (GetWindowThreadProcessId(windowHandle, out var processId) == 0) return new(false, hostHandle, "无法读取桌面层进程。");
        var zOrderAnchor = FindFirstExternalWindowAboveDesktop(hostHandle, processId);
        if (!SetWindowPos(windowHandle, ResolveZOrderAnchor(hostHandle, zOrderAnchor),
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                SwpNoActivate | SwpShowWindow))
            return new(false, 0, "桌面显示层 Z 序定位失败。");

        if (!IsPlacedAboveDesktopContent(windowHandle, hostHandle))
            return new(false, hostHandle, zOrderAnchor, "桌面显示层未能保持在 Explorer 桌面正上方。");
        return new(true, hostHandle, zOrderAnchor, "桌面显示层已位于桌面层级");
    }

    /// <summary>
    /// 把窗口放到所有 MiniC 桌面窗口的最底部，但仍位于 Explorer 桌面之上。
    /// 用于桌面图标层，避免未收纳图标遮挡收纳盒。
    /// </summary>
    public DesktopLayerAttachResult TryPlaceClosestToDesktopContent(nint windowHandle, bool preventActivation = true)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle) || !GetWindowRect(windowHandle, out var bounds))
            return new(false, 0, "桌面图标层窗口尚未创建。");

        var hostHandle = ExplorerDesktopWindowService.FindContentHost();
        if (hostHandle == 0)
            return new(false, 0, "Explorer 没有提供可用的桌面内容宿主。");
        if (GetParent(windowHandle) != 0)
            return new(false, 0, "桌面图标层不是独立顶层窗口。");
        if (!ConfigureAsDesktopToolWindow(windowHandle, preventActivation))
            return new(false, 0, "桌面图标层无法切换为非激活工具窗口。");
        if (IsPlacedAboveDesktopContent(windowHandle, hostHandle))
            return new(true, hostHandle, 0, "桌面图标层层级已正确，无需调整。");

        var zOrderAnchor = GetWindow(hostHandle, GwHwndPrev);
        if (zOrderAnchor != windowHandle
            && !SetWindowPos(windowHandle, ResolveZOrderAnchor(hostHandle, zOrderAnchor),
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                SwpNoActivate | SwpShowWindow))
            return new(false, hostHandle, zOrderAnchor, "桌面图标层 Z 序定位失败。");

        if (!IsPlacedAboveDesktopContent(windowHandle, hostHandle))
            return new(false, hostHandle, zOrderAnchor, "桌面图标层未能贴近 Explorer 桌面。");
        return new(true, hostHandle, zOrderAnchor, "桌面图标层已位于收纳盒下方");
    }

    /// <summary>将正在交互的收纳盒稳定放到 MiniC 桌面窗口最前，但仍位于普通应用下方。</summary>
    public bool BringToFrontWithinDesktopLayer(nint windowHandle, bool preventActivation = true)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle) || !GetWindowRect(windowHandle, out var bounds))
            return false;
        var hostHandle = ExplorerDesktopWindowService.FindContentHost();
        if (hostHandle == 0 || !ConfigureAsDesktopToolWindow(windowHandle, preventActivation)) return false;

        if (GetWindowThreadProcessId(windowHandle, out var processId) == 0) return false;
        var externalWindowAboveLayer = FindFirstExternalWindowAboveDesktop(hostHandle, processId);

        return SetWindowPos(windowHandle,
            ResolveZOrderAnchor(hostHandle, externalWindowAboveLayer),
            bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            SwpNoActivate | SwpShowWindow);
    }

    /// <summary>确认一个顶层窗口在 Z 序中位于另一窗口上方。</summary>
    public bool IsWindowAbove(nint upperWindow, nint lowerWindow)
    {
        if (upperWindow == 0 || lowerWindow == 0
            || !IsWindow(upperWindow) || !IsWindow(lowerWindow)) return false;
        for (var candidate = GetWindow(upperWindow, GwHwndNext);
             candidate != 0;
             candidate = GetWindow(candidate, GwHwndNext))
        {
            if (candidate == lowerWindow) return true;
        }
        return false;
    }

    private static nint ResolveZOrderAnchor(nint desktopHost, nint candidate) =>
        candidate == 0 ? desktopHost : candidate;

    internal static bool ValidateZOrderFallbackForSmokeTest()
    {
        var host = new nint(0x1234);
        var external = new nint(0x5678);
        return ResolveZOrderAnchor(host, 0) == host
               && ResolveZOrderAnchor(host, external) == external;
    }

    private static nint FindFirstExternalWindowAboveDesktop(nint hostHandle, uint desktopLayerProcessId)
    {
        for (var candidate = GetWindow(hostHandle, GwHwndPrev);
             candidate != 0;
             candidate = GetWindow(candidate, GwHwndPrev))
        {
            if (!IsWindowVisible(candidate)) continue;
            if (GetWindowThreadProcessId(candidate, out var candidateProcessId) == 0) continue;
            if (candidateProcessId != desktopLayerProcessId) return candidate;
        }
        return 0;
    }

    /// <summary>确认显示层与 Explorer 桌面宿主之间没有其他应用的可见窗口。</summary>
    public bool IsPlacedAboveDesktopContent(nint windowHandle, nint hostHandle)
    {
        if (windowHandle == 0 || hostHandle == 0 || !IsWindow(windowHandle) || !IsWindow(hostHandle)
            || GetParent(windowHandle) != 0) return false;

        if (GetWindowThreadProcessId(windowHandle, out var desktopLayerProcessId) == 0) return false;
        for (var candidate = GetWindow(windowHandle, GwHwndNext);
             candidate != 0;
             candidate = GetWindow(candidate, GwHwndNext))
        {
            if (candidate == hostHandle) return true;
            if (!IsWindowVisible(candidate)) continue;
            if (GetWindowThreadProcessId(candidate, out var candidateProcessId) == 0) continue;
            if (candidateProcessId != desktopLayerProcessId) return false;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint windowHandle, nint regionHandle, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int combineMode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint windowHandle, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);
}

/// <summary>WorkerW 背景层挂载结果。</summary>
public sealed record DesktopLayerAttachResult(bool IsAttached, nint HostHandle, nint ZOrderAnchor, string Message)
{
    public DesktopLayerAttachResult(bool isAttached, nint hostHandle, string message)
        : this(isAttached, hostHandle, 0, message)
    {
    }
}

/// <summary>窗口的屏幕物理像素边界。</summary>
public sealed record DesktopLayerBounds(int X, int Y, int Width, int Height);
