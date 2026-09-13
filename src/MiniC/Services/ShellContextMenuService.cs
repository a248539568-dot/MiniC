using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MiniC.Services;

/// <summary>调用 Windows Shell 原生右键菜单，确保文件操作与桌面体验一致。</summary>
public sealed class ShellContextMenuService
{
    private const uint CommandFirst = 1;
    private const uint ShellCommandLast = 0x6FFF;
    private const uint CreateGroupCommand = 0x7001;
    private const uint RefreshDesktopCommand = 0x7002;
    private const uint ToggleGroupsCommand = 0x7003;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint NullMessage = 0x0000;
    private const uint CmfNormal = 0x00000000;
    private const uint CmfExtendedVerbs = 0x00000100;
    private const uint CmfCanRename = 0x00000010;
    private const uint CmicMaskUnicode = 0x00004000;
    private const uint CmicMaskPointInvoke = 0x20000000;
    private const uint GcsVerbUnicode = 0x00000004;
    private const uint MfByPosition = 0x00000400;
    private const uint MfPopup = 0x00000010;
    private const uint MfSeparator = 0x00000800;
    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MiimBitmap = 0x00000080;
    private const uint CfHdrop = 15;
    private const int SwShowNormal = 1;
    private static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid ShellViewId = new("000214E3-0000-0000-C000-000000000046");

    public ShellContextMenuResult Show(IntPtr ownerHandle, string path, System.Drawing.Point screenPoint)
        => Show(ownerHandle, [path], screenPoint);

    public static ShellContextMenuResult InvokeVerb(IntPtr ownerHandle, IEnumerable<string> paths, string canonicalVerb, System.Drawing.Point screenPoint)
    {
        var selectedPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selectedPaths.Length == 0) return ShellContextMenuResult.Failed;
        var absolutePidls = new List<IntPtr>(selectedPaths.Length);
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        try
        {
            foreach (var path in selectedPaths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) < 0 || pidl == IntPtr.Zero)
                    return ShellContextMenuResult.Failed;
                absolutePidls.Add(pidl);
            }
            var folderId = ShellFolderId;
            if (SHBindToParent(absolutePidls[0], ref folderId, out parentFolder, out _) < 0 || parentFolder is null)
                return ShellContextMenuResult.Failed;
            var childPidls = absolutePidls.Select(ILFindLastID).ToArray();
            var contextMenuId = ContextMenuId;
            if (parentFolder.GetUIObjectOf(ownerHandle, (uint)childPidls.Length, childPidls,
                    ref contextMenuId, IntPtr.Zero, out var pointer) < 0 || pointer == IntPtr.Zero)
                return ShellContextMenuResult.Failed;
            try { contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            return InvokeShellVerb(contextMenu, canonicalVerb, ownerHandle, screenPoint);
        }
        catch { return ShellContextMenuResult.Failed; }
        finally
        {
            if (contextMenu is not null && Marshal.IsComObject(contextMenu)) Marshal.FinalReleaseComObject(contextMenu);
            if (parentFolder is not null && Marshal.IsComObject(parentFolder)) Marshal.FinalReleaseComObject(parentFolder);
            foreach (var pidl in absolutePidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    public static ShellContextMenuResult PasteToDesktop(System.Drawing.Point screenPoint) =>
        ShellClipboardTransferService.PasteToDesktop(screenPoint)
            ? ShellContextMenuResult.CommandInvoked
            : ShellContextMenuResult.Failed;
    /// <summary>显示 Shell 提供的桌面文件夹背景菜单，不依赖隐藏图标的鼠标命中。</summary>
    public ShellContextMenuResult ShowDesktopBackground(
        IntPtr ownerHandle,
        System.Drawing.Point screenPoint)
    {
        IShellFolder? desktopFolder = null;
        IContextMenu? contextMenu = null;
        object? menuOwner = null;
        try
        {
            contextMenu = CreateDesktopBackgroundContextMenu(
                ownerHandle, out desktopFolder, out menuOwner, out var hasViewCommands);
            if (contextMenu is null) return ShellContextMenuResult.Failed;
            return ShowMenu(ownerHandle, contextMenu, screenPoint,
                interceptRename: false, includeMiniCDesktopCommands: true,
                insertRefreshCommand: !hasViewCommands);
        }
        catch
        {
            return ShellContextMenuResult.Failed;
        }
        finally
        {
            if (contextMenu is not null && Marshal.IsComObject(contextMenu)) Marshal.FinalReleaseComObject(contextMenu);
            if (menuOwner is not null && Marshal.IsComObject(menuOwner)) Marshal.FinalReleaseComObject(menuOwner);
            if (desktopFolder is not null && Marshal.IsComObject(desktopFolder))
                Marshal.FinalReleaseComObject(desktopFolder);
        }
    }

    internal static bool ValidateDesktopBackgroundMenuForSmokeTest()
    {
        IShellFolder? desktopFolder = null;
        IContextMenu? contextMenu = null;
        object? menuOwner = null;
        var menuHandle = IntPtr.Zero;
        var miniCBitmap = IntPtr.Zero;
        try
        {
            contextMenu = CreateDesktopBackgroundContextMenu(
                IntPtr.Zero, out desktopFolder, out menuOwner, out var hasViewCommands);
            if (contextMenu is null || !hasViewCommands) return false;
            menuHandle = CreatePopupMenu();
            if (menuHandle == IntPtr.Zero
                || contextMenu.QueryContextMenu(
                    menuHandle, 0, CommandFirst, ShellCommandLast, CmfNormal) < 0
                || !TryInsertMiniCDesktopCommands(
                    menuHandle, insertRefreshCommand: !hasViewCommands, out miniCBitmap)
                || GetMenuItemCount(menuHandle) <= 3)
                return false;

            var miniCMenu = FindSubMenuByCommand(menuHandle, CreateGroupCommand);
            return miniCMenu != IntPtr.Zero
                   && GetMenuItemId(miniCMenu, 0) == CreateGroupCommand
                   && GetMenuItemId(miniCMenu, 2) == ToggleGroupsCommand
                   && FindSubMenuEntryPosition(menuHandle, CreateGroupCommand) > 1;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (menuHandle != IntPtr.Zero) DestroyMenu(menuHandle);
            if (miniCBitmap != IntPtr.Zero) DeleteObject(miniCBitmap);
            if (contextMenu is not null && Marshal.IsComObject(contextMenu)) Marshal.FinalReleaseComObject(contextMenu);
            if (menuOwner is not null && Marshal.IsComObject(menuOwner)) Marshal.FinalReleaseComObject(menuOwner);
            if (desktopFolder is not null && Marshal.IsComObject(desktopFolder))
                Marshal.FinalReleaseComObject(desktopFolder);
        }
    }

    private static IContextMenu? CreateDesktopBackgroundContextMenu(
        IntPtr ownerHandle,
        out IShellFolder? desktopFolder,
        out object? menuOwner,
        out bool hasViewCommands)
    {
        desktopFolder = null;
        menuOwner = null;
        hasViewCommands = false;
        if (SHGetDesktopFolder(out var folder) < 0 || folder is null) return null;
        desktopFolder = folder;

        var shellViewId = ShellViewId;
        if (folder.CreateViewObject(ownerHandle, ref shellViewId, out var shellViewPointer) >= 0
            && shellViewPointer != IntPtr.Zero)
        {
            IShellView? shellView = null;
            try
            {
                shellView = (IShellView)Marshal.GetObjectForIUnknown(shellViewPointer);
                var viewContextMenuId = ContextMenuId;
                if (shellView.GetItemObject(0, ref viewContextMenuId, out var viewMenuPointer) >= 0
                    && viewMenuPointer != IntPtr.Zero)
                {
                    try
                    {
                        menuOwner = shellView;
                        hasViewCommands = true;
                        return (IContextMenu)Marshal.GetObjectForIUnknown(viewMenuPointer);
                    }
                    finally
                    {
                        Marshal.Release(viewMenuPointer);
                    }
                }
            }
            finally
            {
                Marshal.Release(shellViewPointer);
                if (menuOwner is null && shellView is not null && Marshal.IsComObject(shellView))
                    Marshal.FinalReleaseComObject(shellView);
            }
        }

        var contextMenuId = ContextMenuId;
        if (folder.CreateViewObject(ownerHandle, ref contextMenuId, out var contextMenuPointer) < 0
            || contextMenuPointer == IntPtr.Zero)
            return null;
        try
        {
            return (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPointer);
        }
        finally
        {
            Marshal.Release(contextMenuPointer);
        }
    }

    /// <summary>显示同一目录中一个或多个真实项目的 Shell 菜单。</summary>
    public ShellContextMenuResult Show(
        IntPtr ownerHandle,
        IEnumerable<string> paths,
        System.Drawing.Point screenPoint)
    {
        var selectedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPaths.Length == 0) return ShellContextMenuResult.Failed;

        if (selectedPaths.Length > 1)
        {
            var parentDirectory = Path.GetDirectoryName(selectedPaths[0]);
            if (parentDirectory is null || selectedPaths.Skip(1).Any(path =>
                    !parentDirectory.Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)))
                return ShellContextMenuResult.Failed;
        }

        var absolutePidls = new List<IntPtr>(selectedPaths.Length);
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;

        try
        {
            foreach (var path in selectedPaths)
            {
                var parseResult = SHParseDisplayName(path, IntPtr.Zero, out var absolutePidl, 0, out _);
                if (parseResult < 0 || absolutePidl == IntPtr.Zero) return ShellContextMenuResult.Failed;
                absolutePidls.Add(absolutePidl);
            }

            var folderId = ShellFolderId;
            var result = SHBindToParent(absolutePidls[0], ref folderId, out parentFolder, out _);
            var childPidls = absolutePidls.Select(ILFindLastID).ToArray();
            if (result < 0 || parentFolder is null || childPidls.Any(pidl => pidl == IntPtr.Zero))
                return ShellContextMenuResult.Failed;

            var contextMenuId = ContextMenuId;
            result = parentFolder.GetUIObjectOf(
                ownerHandle,
                (uint)childPidls.Length,
                childPidls,
                ref contextMenuId,
                IntPtr.Zero,
                out var contextMenuPointer);
            if (result < 0 || contextMenuPointer == IntPtr.Zero) return ShellContextMenuResult.Failed;

            try
            {
                contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPointer);
            }
            finally
            {
                Marshal.Release(contextMenuPointer);
            }

            return ShowMenu(ownerHandle, contextMenu, screenPoint,
                interceptRename: true, includeMiniCDesktopCommands: false);
        }
        catch
        {
            return ShellContextMenuResult.Failed;
        }
        finally
        {
            if (contextMenu is not null && Marshal.IsComObject(contextMenu)) Marshal.FinalReleaseComObject(contextMenu);
            if (parentFolder is not null && Marshal.IsComObject(parentFolder)) Marshal.FinalReleaseComObject(parentFolder);
            foreach (var absolutePidl in absolutePidls) Marshal.FreeCoTaskMem(absolutePidl);
        }
    }

    private static ShellContextMenuResult ShowMenu(
        IntPtr ownerHandle,
        IContextMenu contextMenu,
        System.Drawing.Point screenPoint,
        bool interceptRename,
        bool includeMiniCDesktopCommands,
        bool insertRefreshCommand = true)
    {
        var menuHandle = CreatePopupMenu();
        if (menuHandle == IntPtr.Zero) return ShellContextMenuResult.Failed;
        var miniCBitmap = IntPtr.Zero;
        try
        {
            var queryFlags = CmfNormal | (interceptRename ? CmfCanRename : 0);
            if ((System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Shift) != 0)
                queryFlags |= CmfExtendedVerbs;
            if (contextMenu.QueryContextMenu(menuHandle, 0, CommandFirst, ShellCommandLast, queryFlags) < 0)
                return ShellContextMenuResult.Failed;
            if (includeMiniCDesktopCommands && IsShellPasteAvailable())
                EnableShellPasteItem(contextMenu, menuHandle);
            if (includeMiniCDesktopCommands
                && !TryInsertMiniCDesktopCommands(
                    menuHandle, insertRefreshCommand, out miniCBitmap))
                return ShellContextMenuResult.Failed;

            var menu2 = contextMenu as IContextMenu2;
            var menu3 = contextMenu as IContextMenu3;
            var source = HwndSource.FromHwnd(ownerHandle);
            HwndSourceHook hook = (IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                ForwardMenuMessage(menu2, menu3, message, wParam, lParam, ref handled);
            source?.AddHook(hook);
            uint selectedCommand;
            try
            {
                _ = SetForegroundWindow(ownerHandle);
                selectedCommand = TrackPopupMenuEx(menuHandle, TpmRightButton | TpmReturnCommand,
                    screenPoint.X, screenPoint.Y, ownerHandle, IntPtr.Zero);
                _ = PostMessage(ownerHandle, NullMessage, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                source?.RemoveHook(hook);
            }
            if (selectedCommand == 0) return ShellContextMenuResult.Dismissed;

            if (selectedCommand == CreateGroupCommand) return ShellContextMenuResult.CreateGroupRequested;
            if (selectedCommand == RefreshDesktopCommand) return ShellContextMenuResult.RefreshRequested;
            if (selectedCommand == ToggleGroupsCommand) return ShellContextMenuResult.ToggleGroupsRequested;
            var isNewMenuCommand = IsCommandInNewSubmenu(contextMenu, menuHandle, selectedCommand);
            var commandOffset = selectedCommand - CommandFirst;
            var canonicalVerb = GetCanonicalVerb(contextMenu, commandOffset);
            if (canonicalVerb.Equals("paste", StringComparison.OrdinalIgnoreCase))
                return PasteToDesktop(screenPoint);
            if (interceptRename
                && canonicalVerb.Equals("rename", StringComparison.OrdinalIgnoreCase))
            {
                return ShellContextMenuResult.RenameRequested;
            }

            var result = InvokeShellCommand(contextMenu, commandOffset, ownerHandle, screenPoint);
            if (result != ShellContextMenuResult.CommandInvoked) return result;
            if (isNewMenuCommand || canonicalVerb.StartsWith("new", StringComparison.OrdinalIgnoreCase))
                return ShellContextMenuResult.NewItemCreated;
            if (canonicalVerb.Equals("copy", StringComparison.OrdinalIgnoreCase))
                return ShellContextMenuResult.CopyInvoked;
            if (canonicalVerb.Equals("cut", StringComparison.OrdinalIgnoreCase))
                return ShellContextMenuResult.CutInvoked;
            return result;
        }
        finally
        {
            DestroyMenu(menuHandle);
            if (miniCBitmap != IntPtr.Zero) DeleteObject(miniCBitmap);
        }
    }

    private static ShellContextMenuResult InvokeShellCommand(
        IContextMenu contextMenu,
        uint commandOffset,
        IntPtr ownerHandle,
        System.Drawing.Point screenPoint)
    {
        var invoke = new CommandInfoEx
        {
            Size = Marshal.SizeOf<CommandInfoEx>(),
            Mask = CmicMaskUnicode | CmicMaskPointInvoke,
            Window = ownerHandle,
            Verb = new IntPtr(commandOffset),
            VerbUnicode = new IntPtr(commandOffset),
            Show = SwShowNormal,
            InvokePoint = new NativePoint(screenPoint.X, screenPoint.Y)
        };
        return contextMenu.InvokeCommand(ref invoke) >= 0
            ? ShellContextMenuResult.CommandInvoked
            : ShellContextMenuResult.Failed;
    }

    private static ShellContextMenuResult InvokeShellVerb(
        IContextMenu contextMenu,
        string canonicalVerb,
        IntPtr ownerHandle,
        System.Drawing.Point screenPoint)
    {
        var ansiVerb = Marshal.StringToHGlobalAnsi(canonicalVerb);
        var unicodeVerb = Marshal.StringToHGlobalUni(canonicalVerb);
        try
        {
            var invoke = new CommandInfoEx
            {
                Size = Marshal.SizeOf<CommandInfoEx>(),
                Mask = CmicMaskUnicode | CmicMaskPointInvoke,
                Window = ownerHandle,
                Verb = ansiVerb,
                VerbUnicode = unicodeVerb,
                Show = SwShowNormal,
                InvokePoint = new NativePoint(screenPoint.X, screenPoint.Y)
            };
            return contextMenu.InvokeCommand(ref invoke) >= 0
                ? ShellContextMenuResult.CommandInvoked
                : ShellContextMenuResult.Failed;
        }
        finally
        {
            Marshal.FreeHGlobal(ansiVerb);
            Marshal.FreeHGlobal(unicodeVerb);
        }
    }

    private static bool IsShellPasteAvailable()
    {
        try
        {
            if (IsClipboardFormatAvailable(CfHdrop)) return true;
            return ShellPasteFormats.Any(format =>
            {
                var id = RegisterClipboardFormat(format);
                return id != 0 && IsClipboardFormatAvailable(id);
            });
        }
        catch
        {
            return false;
        }
    }

    private static void EnableShellPasteItem(IContextMenu contextMenu, IntPtr menuHandle)
    {
        var count = GetMenuItemCount(menuHandle);
        for (var index = 0; index < count; index++)
        {
            var commandId = GetMenuItemId(menuHandle, index);
            if (commandId < CommandFirst || commandId > ShellCommandLast) continue;
            var canonicalVerb = GetCanonicalVerb(contextMenu, commandId - CommandFirst);
            if (!canonicalVerb.Equals("paste", StringComparison.OrdinalIgnoreCase)) continue;
            _ = EnableMenuItem(menuHandle, (uint)index, MfByPosition);
            return;
        }
    }

    private static readonly string[] ShellPasteFormats =
    [
        System.Windows.DataFormats.FileDrop,
        "Shell IDList Array",
        "FileGroupDescriptorW",
        "FileGroupDescriptor"
    ];

    private static bool TryInsertMiniCDesktopCommands(
        IntPtr menuHandle,
        bool insertRefreshCommand,
        out IntPtr miniCBitmap)
    {
        miniCBitmap = IntPtr.Zero;
        var miniCMenu = CreatePopupMenu();
        if (miniCMenu == IntPtr.Zero) return false;

        var inserted = false;
        try
        {
            if (!AppendMenu(miniCMenu, MfString, new IntPtr(CreateGroupCommand), "新建收纳盒")
                || !AppendMenu(miniCMenu, MfSeparator, IntPtr.Zero, string.Empty)
                || !AppendMenu(miniCMenu, MfString, new IntPtr(ToggleGroupsCommand), "显示 / 隐藏收纳盒"))
                return false;

            var insertPosition = FindMiniCInsertPosition(menuHandle);
            if (!InsertMenu(menuHandle, insertPosition, MfByPosition | MfPopup, miniCMenu, "MiniC"))
                return false;

            inserted = true;
            if (insertRefreshCommand && !InsertMenu(menuHandle, 0, MfByPosition | MfString,
                    new IntPtr(RefreshDesktopCommand), "刷新桌面"))
                return false;

            miniCBitmap = CreateApplicationMenuBitmap();
            if (miniCBitmap == IntPtr.Zero) return false;
            var menuItemInfo = new MenuItemInfo
            {
                Size = (uint)Marshal.SizeOf<MenuItemInfo>(),
                Mask = MiimBitmap,
                BitmapItem = miniCBitmap
            };
            var miniCPosition = FindSubMenuEntryPosition(menuHandle, CreateGroupCommand);
            if (miniCPosition >= 0 && SetMenuItemInfo(menuHandle, (uint)miniCPosition, true, ref menuItemInfo)) return true;

            DeleteObject(miniCBitmap);
            miniCBitmap = IntPtr.Zero;
            return false;
        }
        finally
        {
            // 成功插入后子菜单由根菜单拥有，DestroyMenu(root) 会统一释放。
            if (!inserted) DestroyMenu(miniCMenu);
        }
    }

    private static uint FindMiniCInsertPosition(IntPtr menuHandle)
    {
        var count = GetMenuItemCount(menuHandle);
        for (var index = 0; index < count; index++)
        {
            var text = GetMenuItemText(menuHandle, index);
            if (text.Contains("新建", StringComparison.CurrentCultureIgnoreCase))
                return (uint)(index > 0 && GetMenuItemId(menuHandle, index - 1) == 0 ? index - 1 : index);
        }
        return (uint)count;
    }

    private static int FindMenuItemByCommand(IntPtr menuHandle, uint command)
    {
        for (var index = 0; index < GetMenuItemCount(menuHandle); index++)
            if (GetMenuItemId(menuHandle, index) == command) return index;
        return -1;
    }

    private static IntPtr FindSubMenuByCommand(IntPtr menuHandle, uint command)
    {
        for (var index = 0; index < GetMenuItemCount(menuHandle); index++)
        {
            var submenu = GetSubMenu(menuHandle, index);
            if (submenu != IntPtr.Zero && FindMenuItemByCommand(submenu, command) >= 0) return submenu;
        }
        return IntPtr.Zero;
    }

    private static int FindSubMenuEntryPosition(IntPtr menuHandle, uint command)
    {
        for (var index = 0; index < GetMenuItemCount(menuHandle); index++)
        {
            var submenu = GetSubMenu(menuHandle, index);
            if (submenu != IntPtr.Zero && FindMenuItemByCommand(submenu, command) >= 0) return index;
        }
        return -1;
    }

    private static bool IsCommandInNewSubmenu(
        IContextMenu contextMenu,
        IntPtr rootMenu,
        uint selectedCommand)
    {
        for (var index = 0; index < GetMenuItemCount(rootMenu); index++)
        {
            var submenu = GetSubMenu(rootMenu, index);
            if (submenu == IntPtr.Zero
                || !ContainsCommand(submenu, selectedCommand)
                || !ContainsNewCanonicalVerb(contextMenu, submenu)) continue;
            return true;
        }
        return false;
    }

    private static bool ContainsCommand(IntPtr menu, uint command)
    {
        for (var index = 0; index < GetMenuItemCount(menu); index++)
        {
            if (GetMenuItemId(menu, index) == command) return true;
            var submenu = GetSubMenu(menu, index);
            if (submenu != IntPtr.Zero && ContainsCommand(submenu, command)) return true;
        }
        return false;
    }

    private static bool ContainsNewCanonicalVerb(IContextMenu contextMenu, IntPtr menu)
    {
        for (var index = 0; index < GetMenuItemCount(menu); index++)
        {
            var command = GetMenuItemId(menu, index);
            if (command is >= CommandFirst and <= ShellCommandLast
                && GetCanonicalVerb(contextMenu, command - CommandFirst)
                    .StartsWith("new", StringComparison.OrdinalIgnoreCase)) return true;
            var submenu = GetSubMenu(menu, index);
            if (submenu != IntPtr.Zero && ContainsNewCanonicalVerb(contextMenu, submenu)) return true;
        }
        return false;
    }

    private static string GetMenuItemText(IntPtr menuHandle, int position)
    {
        var buffer = new System.Text.StringBuilder(256);
        _ = GetMenuString(menuHandle, (uint)position, buffer, buffer.Capacity, MfByPosition);
        return buffer.ToString();
    }

    private static IntPtr CreateApplicationMenuBitmap()
    {
        try
        {
            var applicationPath = Path.Combine(AppContext.BaseDirectory, "MiniC.exe");
            if (!File.Exists(applicationPath)) applicationPath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(applicationPath)) return IntPtr.Zero;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(applicationPath);
            if (icon is null) return IntPtr.Zero;
            using var bitmap = new System.Drawing.Bitmap(16, 16,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.DrawIcon(icon, new System.Drawing.Rectangle(0, 0, 16, 16));
            }
            return bitmap.GetHbitmap(System.Drawing.Color.Transparent);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string GetCanonicalVerb(IContextMenu contextMenu, uint commandOffset)
    {
        const int characterCapacity = 128;
        var buffer = Marshal.AllocHGlobal(characterCapacity * sizeof(char));
        try
        {
            return contextMenu.GetCommandString(new UIntPtr(commandOffset), GcsVerbUnicode,
                       IntPtr.Zero, buffer, characterCapacity) >= 0
                ? Marshal.PtrToStringUni(buffer) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr ForwardMenuMessage(
        IContextMenu2? menu2,
        IContextMenu3? menu3,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message is not (0x0117 or 0x002B or 0x002C or 0x0120)) return IntPtr.Zero;

        if (menu3 is not null && menu3.HandleMenuMsg2((uint)message, wParam, lParam, out var result) == 0)
        {
            handled = true;
            return result;
        }
        if (menu2 is not null && menu2.HandleMenuMsg((uint)message, wParam, lParam) == 0)
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y) { X = x; Y = y; }
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfoEx
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Title;
        public IntPtr VerbUnicode;
        public IntPtr ParametersUnicode;
        public IntPtr DirectoryUnicode;
        public IntPtr TitleUnicode;
        public NativePoint InvokePoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public IntPtr SubMenu;
        public IntPtr BitmapChecked;
        public IntPtr BitmapUnchecked;
        public UIntPtr ItemData;
        public IntPtr TypeData;
        public uint TextLength;
        public IntPtr BitmapItem;
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr windowHandle);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        [PreserveSig] int TranslateAccelerator(IntPtr message);
        [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr previousView, IntPtr folderSettings, IntPtr shellBrowser, IntPtr bounds, out IntPtr windowHandle);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(IntPtr folderSettings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr parameter);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr itemIdList, uint flags);
        [PreserveSig] int GetItemObject(uint item, ref Guid interfaceId, out IntPtr result);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr window, IntPtr bindContext, IntPtr displayName, ref uint eaten, out IntPtr itemIdList, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr window, uint flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr itemIdList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr itemIdList, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr parameter, IntPtr firstIdList, IntPtr secondIdList);
        [PreserveSig] int CreateViewObject(IntPtr window, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, IntPtr[] itemIdLists, ref uint attributes);
        [PreserveSig]
        int GetUIObjectOf(
            IntPtr window,
            uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] itemIdLists,
            ref Guid interfaceId,
            IntPtr reserved,
            out IntPtr result);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint commandFirst, uint commandLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfoEx commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint maximum);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig] new int QueryContextMenu(IntPtr menu, uint index, uint commandFirst, uint commandLast, uint flags);
        [PreserveSig] new int InvokeCommand(ref CommandInfoEx commandInfo);
        [PreserveSig] new int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint maximum);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig] new int QueryContextMenu(IntPtr menu, uint index, uint commandFirst, uint commandLast, uint flags);
        [PreserveSig] new int InvokeCommand(ref CommandInfoEx commandInfo);
        [PreserveSig] new int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, uint maximum);
        [PreserveSig] new int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder desktopFolder);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr itemIdList, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellFolder parent, out IntPtr childIdList);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr itemIdList);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll", EntryPoint = "GetMenuItemID")]
    private static extern uint GetMenuItemId(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder text, int maximum, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr itemOrSubmenu, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InsertMenu(IntPtr menu, uint position, uint flags, IntPtr itemOrSubmenu, string text);

    [DllImport("user32.dll")]
    private static extern uint EnableMenuItem(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetMenuItemInfo(
        IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo menuItemInfo);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr graphicsObject);

}

/// <summary>Windows Shell 菜单关闭后的操作结果。</summary>
public enum ShellContextMenuResult
{
    Failed,
    Dismissed,
    CommandInvoked,
    NewItemCreated,
    CopyInvoked,
    CutInvoked,
    RenameRequested,
    CreateGroupRequested,
    RefreshRequested,
    ToggleGroupsRequested
}
