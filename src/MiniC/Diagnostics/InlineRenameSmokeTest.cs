using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MiniC.Diagnostics;

/// <summary>在临时目录与离屏窗口验证两种文件编辑器的真实失焦提交，不操作用户桌面。</summary>
internal static class InlineRenameSmokeTest
{
    public static async Task<bool> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-RenameFocus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DesktopSurfaceWindow? desktop = null;
        DeskGroupWindow? box = null;
        try
        {
            using var files = new DesktopFileService(root, root);
            var calls = 0;
            async Task<bool> Rename(DesktopItem item, string name)
            {
                calls++;
                await Task.Delay(30); // 确认异步提交完成时不会抢回新控件的焦点。
                var result = await files.RenameItemAsync(item.Path, name, extensionsVisible: false);
                if (result.Succeeded && result.NewPath is not null)
                {
                    item.Path = result.NewPath;
                    item.Name = Path.GetFileNameWithoutExtension(result.NewPath);
                }
                return result.Succeeded;
            }
            var item = new DesktopItem { Path = Path.Combine(root, "desktop.txt"), Name = "desktop" };
            await File.WriteAllTextAsync(item.Path, "desktop sample");
            desktop = new DesktopSurfaceWindow(new ObservableCollection<DesktopItem> { item },
                new DesktopSurfaceWindowCallbacks((_, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask, Rename, (_, _, _) => Task.CompletedTask, () => { }, _ => Task.CompletedTask),
                new DesktopLayerHostService(), new DesktopDragStateService());
            var desktopWorks = await ValidateAsync(desktop, item, desktop.BeginRename, "desktop-saved");
            desktop.ClosePermanently(); desktop = null;

            var groupItem = new DesktopItem { Path = Path.Combine(root, "group.txt"), Name = "group" };
            await File.WriteAllTextAsync(groupItem.Path, "group sample");
            var group = new DeskGroup { Name = "Rename check", Width = 380, Height = 280 };
            group.Items.Add(groupItem);
            box = new DeskGroupWindow(group, new DeskGroupWindowCallbacks(_ => { },
                (_, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, value, name) => Rename(value, name), (_, _, _, _) => Task.CompletedTask,
                _ => { }, _ => { }, _ => { }, _ => { }, _ => [], (_, _) => null, (_, _) => { },
                (_, _) => { }, (_, _) => { }, (_, _) => { }, _ => { }),
                new DesktopLayerHostService(), new DesktopDragStateService());
            var groupWorks = await ValidateAsync(box, groupItem, box.BeginRename, "group-saved");
            return desktopWorks && groupWorks && calls == 4;
        }
        finally
        {
            desktop?.ClosePermanently();
            box?.ClosePermanently();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> ValidateAsync(Window window, DesktopItem item, Action<DesktopItem> beginRename, string name)
    {
        window.Left = -24000; window.Top = -24000; window.Width = 380; window.Height = 280;
        window.Show();
        var surface = (Grid)window.FindName("SelectionSurface");
        var target = new TextBox { Width = 1, Height = 1, Opacity = 0 };
        surface.Children.Add(target);
        try
        {
            beginRename(item);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var editor = FindEditor(window, item);
            if (editor is null || !editor.IsKeyboardFocusWithin) return false;
            editor.Text = name;
            Keyboard.Focus(target);
            if (!await WaitForCommitAsync(item) || item.Name != name || !target.IsKeyboardFocusWithin
                || !File.Exists(item.Path)) return false;

            beginRename(item);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            editor = FindEditor(window, item)!;
            editor.Text = "cancelled-draft";
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor),
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Keyboard.Focus(target);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (item.IsRenaming || item.Name != name) return false;

            beginRename(item);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            editor = FindEditor(window, item)!;
            editor.Text = ".";
            Keyboard.Focus(target);
            return await WaitForCommitAsync(item) && item.Name == name && File.Exists(item.Path)
                   && item.EditName == DesktopFileService.GetRenameEditName(item) && target.IsKeyboardFocusWithin;
        }
        finally { surface.Children.Remove(target); }
    }

    private static async Task<bool> WaitForCommitAsync(DesktopItem item)
    {
        for (var attempt = 0; attempt < 100 && item.IsRenaming; attempt++) await Task.Delay(20);
        return !item.IsRenaming;
    }

    private static TextBox? FindEditor(DependencyObject parent, DesktopItem item)
    {
        if (parent is TextBox { Name: "RenameBox" } editor && ReferenceEquals(editor.DataContext, item)) return editor;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindEditor(VisualTreeHelper.GetChild(parent, index), item) is { } found) return found;
        return null;
    }
}
