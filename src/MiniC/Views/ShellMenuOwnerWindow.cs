using System.Windows;
using System.Windows.Interop;

namespace MiniC.Views;

/// <summary>为系统 Shell 菜单提供独立前台句柄，避免激活桌面图标层。</summary>
public sealed class ShellMenuOwnerWindow : Window
{
    public ShellMenuOwnerWindow(System.Drawing.Point screenPoint)
    {
        Width = 1;
        Height = 1;
        Left = screenPoint.X;
        Top = screenPoint.Y;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Opacity = 0;
        ShowInTaskbar = false;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
    }

    public nint ShowAndGetHandle()
    {
        Show();
        Activate();
        return new WindowInteropHelper(this).Handle;
    }
}
