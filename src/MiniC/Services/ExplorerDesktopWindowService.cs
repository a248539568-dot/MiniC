using System.Runtime.InteropServices;
using System.Text;

namespace MiniC.Services;

/// <summary>只识别 Explorer 真正的桌面视图，排除打开或保存对话框中的 Shell 文件视图。</summary>
internal static class ExplorerDesktopWindowService
{
    public static nint FindContentHost()
    {
        var shellWindow = GetShellWindow();
        if (shellWindow == 0) return 0;
        if (GetWindowThreadProcessId(shellWindow, out var shellProcessId) == 0 || shellProcessId == 0) return 0;
        if (IsContentHostCandidate(shellWindow, shellProcessId)) return shellWindow;

        var result = (nint)0;
        EnumWindows((topLevel, _) =>
        {
            if (!IsContentHostCandidate(topLevel, shellProcessId)) return true;
            result = topLevel;
            return false;
        }, 0);
        return result;
    }

    public static nint FindListView()
    {
        var host = FindContentHost();
        if (host == 0) return 0;
        var shellView = FindWindowEx(host, 0, "SHELLDLL_DefView", null);
        if (shellView == 0) return 0;
        var listView = FindWindowEx(shellView, 0, "SysListView32", "FolderView");
        return listView != 0 ? listView : FindWindowEx(shellView, 0, "SysListView32", null);
    }

    private static bool IsContentHostCandidate(nint windowHandle, uint shellProcessId)
    {
        if (windowHandle == 0 || FindWindowEx(windowHandle, 0, "SHELLDLL_DefView", null) == 0)
            return false;
        if (GetWindowThreadProcessId(windowHandle, out var processId) == 0) return false;
        if (processId != shellProcessId) return false;
        var className = new StringBuilder(64);
        if (GetClassName(windowHandle, className, className.Capacity) == 0) return false;
        return className.ToString() is "Progman" or "WorkerW";
    }

    internal static bool ValidateForSmokeTest()
    {
        var host = FindContentHost();
        var shellWindow = GetShellWindow();
        if (host == 0 || shellWindow == 0 || FindListView() == 0) return false;
        if (GetWindowThreadProcessId(shellWindow, out var shellProcessId) == 0) return false;
        return IsContentHostCandidate(host, shellProcessId);
    }

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
