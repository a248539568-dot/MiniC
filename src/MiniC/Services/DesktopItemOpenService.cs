using System.Diagnostics;

namespace MiniC.Services;

/// <summary>统一处理桌面和收纳盒项目的 Windows Shell 打开行为。</summary>
public static class DesktopItemOpenService
{
    public static bool TryOpen(string path, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}

/// <summary>不依赖窗口激活状态，按 Windows 双击时间与距离识别连续点击。</summary>
public sealed class DesktopItemOpenGestureTracker
{
    private string? _lastPath;
    private int _lastTimestamp;
    private System.Drawing.Point _lastScreenPoint;
    private string? _pressedPath;
    private bool _opensOnRelease;

    public bool IsPressed(string path) => _pressedPath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true;

    public void RegisterMouseDown(string path, System.Drawing.Point screenPoint, int timestamp)
    {
        if (_pressedPath is not null) Reset(); // 上次按下未在原项目释放，不构成完整点击。
        var elapsed = unchecked((uint)(timestamp - _lastTimestamp));
        var doubleClickSize = System.Windows.Forms.SystemInformation.DoubleClickSize;
        var isDoubleClick = _lastPath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true
                            && elapsed <= System.Windows.Forms.SystemInformation.DoubleClickTime
                            && Math.Abs(screenPoint.X - _lastScreenPoint.X) <= doubleClickSize.Width / 2
                            && Math.Abs(screenPoint.Y - _lastScreenPoint.Y) <= doubleClickSize.Height / 2;
        _pressedPath = path;
        _opensOnRelease = isDoubleClick;
        _lastPath = path;
        _lastTimestamp = timestamp;
        _lastScreenPoint = screenPoint;
    }

    public bool RegisterMouseUp(string path)
    {
        if (!IsPressed(path))
        {
            Reset();
            return false;
        }
        var opens = _opensOnRelease;
        _pressedPath = null;
        _opensOnRelease = false;
        if (opens) Reset();
        return opens;
    }

    public void Reset()
    {
        _lastPath = null;
        _lastTimestamp = 0;
        _lastScreenPoint = default;
        _pressedPath = null;
        _opensOnRelease = false;
    }

    internal static bool ValidateForSmokeTest()
    {
        var tracker = new DesktopItemOpenGestureTracker();
        var point = new System.Drawing.Point(120, 80);
        tracker.RegisterMouseDown(@"C:\Temp\a.txt", point, 1000);
        if (tracker.RegisterMouseUp(@"C:\Temp\a.txt")) return false;
        tracker.RegisterMouseDown(@"C:\Temp\a.txt", point, 1100);
        if (!tracker.RegisterMouseUp(@"C:\Temp\a.txt")) return false;
        tracker.RegisterMouseDown(@"C:\Temp\a.txt", point, 1200);
        tracker.Reset(); // 拖动取消整次点击。
        return !tracker.RegisterMouseUp(@"C:\Temp\a.txt");
    }
}
