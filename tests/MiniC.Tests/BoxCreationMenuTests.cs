using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MiniC.Services;
using MiniC.Widgets;
using Xunit;

namespace MiniC.Tests;

public sealed class BoxCreationMenuTests
{
    private static readonly string[] ExpectedLabels = ["新建收纳盒", "新建天气", "新建日程中心"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopNativeMenuIncludesEverySupportedBoxWithCorrectCommands(bool addRefresh)
    {
        var root = CreatePopupMenu();
        Assert.NotEqual(IntPtr.Zero, root);
        var bitmap = IntPtr.Zero;
        try
        {
            Assert.True(ShellContextMenuService.TryInsertMiniCDesktopCommands(root, addRefresh, out bitmap));
            Assert.Equal(addRefresh ? 2 : 1, GetMenuItemCount(root));
            var miniCPosition = addRefresh ? 1 : 0;
            Assert.Equal("MiniC", Label(root, miniCPosition));
            var menu = GetSubMenu(root, miniCPosition);
            Assert.NotEqual(IntPtr.Zero, menu);
            Assert.Equal(ExpectedLabels,
                Enumerable.Range(0, 3).Select(index => Label(menu, index)));
            Assert.Equal(BoxCreationCatalog.Options.Count + 2, GetMenuItemCount(menu));
            foreach (var (option, index) in BoxCreationCatalog.Options.Select((option, index) => (option, index)))
            {
                var command = GetMenuItemID(menu, index);
                Assert.True(command > 0x6FFF, "MiniC creation commands must not overlap Shell extension IDs.");
                Assert.Same(option, BoxCreationCatalog.Find(command));
                Assert.Same(option, BoxCreationCatalog.Find(option.Result));
            }
            Assert.Null(BoxCreationCatalog.Options[0].WidgetKind);
            Assert.Equal(WidgetKind.Weather, BoxCreationCatalog.Find(GetMenuItemID(menu, 1))!.WidgetKind);
            Assert.Equal(WidgetKind.Planner, BoxCreationCatalog.Find(GetMenuItemID(menu, 2))!.WidgetKind);
            Assert.Equal("显示 / 隐藏收纳盒", Label(menu, GetMenuItemCount(menu) - 1));
            Assert.Equal(0x7003u, GetMenuItemID(menu, GetMenuItemCount(menu) - 1));
            if (addRefresh) Assert.Equal(0x7002u, GetMenuItemID(root, 0));
            Assert.Null(BoxCreationCatalog.Find(0x7002u));
            Assert.Null(BoxCreationCatalog.Find(0u));
            Assert.Equal(BoxCreationCatalog.Options.Count, BoxCreationCatalog.Options.Select(option => option.CommandId).Distinct().Count());
        }
        finally
        {
            DestroyMenu(root);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }

    private static string Label(IntPtr menu, int position)
    {
        var buffer = new StringBuilder(128);
        Assert.True(GetMenuString(menu, (uint)position, buffer, buffer.Capacity, 0x400) > 0);
        return buffer.ToString();
    }

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int maximum, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}
