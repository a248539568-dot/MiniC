using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using MiniC.Models;

namespace MiniC.Services;

/// <summary>调用 Windows Shell 原生右键菜单，确保文件操作与桌面体验一致。</summary>
public sealed class ShellContextMenuService
{
    private const uint CommandFirst = 1;
    private const uint ShellCommandLast = 0x6FFF;
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
    private const uint MfChecked = 0x00000008;
    private const uint MiimBitmap = 0x00000080;
    private const uint CfHdrop = 15;
    private const int SwShowNormal = 1;
    private static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid ShellViewId = new("000214E3-0000-0000-C000-000000000046");

    private static ShellContextMenuResult Failure(string operation, int? hresult = null)
    {
        var suffix = hresult is null ? string.Empty : $" HRESULT=0x{hresult.Value:X8}";
        MiniCLogger.Warning(nameof(ShellContextMenuService), $"Shell command failed: {operation}.{suffix}");
        return ShellContextMenuResult.Failed;
    }

    public ShellContextMenuResult Show(IntPtr ownerHandle, string path, System.Drawing.Point screenPoint)
        => Show(ownerHandle, [path], screenPoint);

    public static ShellContextMenuResult InvokeVerb(IntPtr ownerHandle, IEnumerable<string> paths, string canonicalVerb, System.Drawing.Point screenPoint)
    {
        var selectedPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selectedPaths.Length == 0) return Failure("InvokeVerb received no paths");
        var absolutePidls = new List<IntPtr>(selectedPaths.Length);
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        try
        {
            foreach (var path in selectedPaths)
            {
                var parseResult = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
                if (parseResult < 0 || pidl == IntPtr.Zero)
                    return Failure($"InvokeVerb parse path '{path}'", parseResult);
                absolutePidls.Add(pidl);
            }
            var folderId = ShellFolderId;
            var bindResult = SHBindToParent(absolutePidls[0], ref folderId, out parentFolder, out _);
            if (bindResult < 0 || parentFolder is null)
                return Failure("InvokeVerb bind parent", bindResult);
            var childPidls = absolutePidls.Select(ILFindLastID).ToArray();
            var contextMenuId = ContextMenuId;
            var uiResult = parentFolder.GetUIObjectOf(ownerHandle, (uint)childPidls.Length, childPidls,
                ref contextMenuId, IntPtr.Zero, out var pointer);
            if (uiResult < 0 || pointer == IntPtr.Zero)
                return Failure("InvokeVerb create context menu", uiResult);
            try { contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            return InvokeShellVerb(contextMenu, canonicalVerb, ownerHandle, screenPoint);
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(ShellContextMenuService), exception, "InvokeVerb failed.");
            return ShellContextMenuResult.Failed;
        }
        finally
        {
            if (contextMenu is not null && Marshal.IsComObject(contextMenu)) Marshal.FinalReleaseComObject(contextMenu);
            if (parentFolder is not null && Marshal.IsComObject(parentFolder)) Marshal.FinalReleaseComObject(parentFolder);
            foreach (var pidl in absolutePidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    public static ShellContextMenuResult PasteToDesktop(System.Drawing.Point screenPoint)
    {
        if (ShellClipboardTransferService.PasteToDesktop(screenPoint))
            return ShellContextMenuResult.CommandInvoked;
        return Failure("Paste to desktop");
    }
    /// <summary>显示 Shell 提供的桌面文件夹背景菜单，不依赖隐藏图标的鼠标命中。</summary>
    public ShellContextMenuResult ShowDesktopBackground(
        IntPtr ownerHandle,
        System.Drawing.Point screenPoint,
        NativeDesktopState? desktopState,
        bool includeMiniCDesktopCommands = true)
    {
        IShellFolder? desktopFolder = null;
        IContextMenu? contextMenu = null;
        object? menuOwner = null;
        try
        {
            contextMenu = CreateDesktopBackgroundContextMenu(
                ownerHandle, out desktopFolder, out menuOwner, out _);
            if (contextMenu is null) return Failure("Create desktop background context menu");
            return ShowMenu(ownerHandle, contextMenu, screenPoint,
                interceptRename: false, includeMiniCDesktopCommands,
                desktopState,
                insertRefreshCommand: true);
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(ShellContextMenuService), exception, "Desktop background menu failed.");
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

    private static IContextMenu? CreateDesktopBackgroundContextMenu(
        IntPtr ownerHandle,
        out IShellFolder? desktopFolder,
        out object? menuOwner,
        out bool hasViewCommands)
    {
        desktopFolder = null;
        menuOwner = null;
        hasViewCommands = false;
        var desktopResult = SHGetDesktopFolder(out var folder);
        if (desktopResult < 0 || folder is null)
        {
            MiniCLogger.Warning(nameof(ShellContextMenuService),
                $"Shell desktop folder unavailable. HRESULT=0x{desktopResult:X8}.");
            return null;
        }
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
        if (selectedPaths.Length == 0) return Failure("Show received no paths");

        if (selectedPaths.Length > 1)
        {
            var parentDirectory = Path.GetDirectoryName(selectedPaths[0]);
            if (parentDirectory is null || selectedPaths.Skip(1).Any(path =>
                    !parentDirectory.Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)))
                return Failure("Show received paths from different directories");
        }

        var absolutePidls = new List<IntPtr>(selectedPaths.Length);
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;

        try
        {
            foreach (var path in selectedPaths)
            {
                var parseResult = SHParseDisplayName(path, IntPtr.Zero, out var absolutePidl, 0, out _);
                if (parseResult < 0 || absolutePidl == IntPtr.Zero)
                    return Failure($"Show parse path '{path}'", parseResult);
                absolutePidls.Add(absolutePidl);
            }

            var folderId = ShellFolderId;
            var result = SHBindToParent(absolutePidls[0], ref folderId, out parentFolder, out _);
            var childPidls = absolutePidls.Select(ILFindLastID).ToArray();
            if (result < 0 || parentFolder is null || childPidls.Any(pidl => pidl == IntPtr.Zero))
                return Failure("Show bind parent or child PIDL", result);

            var contextMenuId = ContextMenuId;
            result = parentFolder.GetUIObjectOf(
                ownerHandle,
                (uint)childPidls.Length,
                childPidls,
                ref contextMenuId,
                IntPtr.Zero,
                out var contextMenuPointer);
            if (result < 0 || contextMenuPointer == IntPtr.Zero)
                return Failure("Show create context menu", result);

            try
            {
                contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPointer);
            }
            finally
            {
                Marshal.Release(contextMenuPointer);
            }

            return ShowMenu(ownerHandle, contextMenu, screenPoint,
                interceptRename: true, includeMiniCDesktopCommands: false,
                insertRefreshCommand: false);
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(ShellContextMenuService), exception, "Item context menu failed.");
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
        NativeDesktopState? desktopState = null,
        bool insertRefreshCommand = false)
    {
        var menuHandle = CreatePopupMenu();
        if (menuHandle == IntPtr.Zero) return Failure("CreatePopupMenu");
        var miniCBitmap = IntPtr.Zero;
        try
        {
            var queryFlags = CmfNormal | (interceptRename ? CmfCanRename : 0);
            if ((System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Shift) != 0)
                queryFlags |= CmfExtendedVerbs;
            var queryResult = contextMenu.QueryContextMenu(menuHandle, 0, CommandFirst, ShellCommandLast, queryFlags);
            if (queryResult < 0)
                return Failure("QueryContextMenu", queryResult);
            if (desktopState is not null)
                ReplaceDesktopLayoutMenus(menuHandle, desktopState);
            else if (insertRefreshCommand)
                RemoveDesktopLayoutMenus(menuHandle);
            if (insertRefreshCommand && !InsertDesktopRefreshCommand(contextMenu, menuHandle))
                return Failure("Insert MiniC refresh command");
            if ((desktopState is not null || insertRefreshCommand) && IsShellPasteAvailable())
                EnableShellPasteItem(contextMenu, menuHandle);
            if (includeMiniCDesktopCommands
                && !TryInsertMiniCDesktopCommands(
                    menuHandle, insertRefreshCommand: false, out miniCBitmap))
                return Failure("Insert MiniC desktop commands");

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

            if (desktopState is not null && TryMapDesktopLayoutCommand(selectedCommand, out var desktopResult))
                return desktopResult;

            if (includeMiniCDesktopCommands && BoxCreationCatalog.Find(selectedCommand) is { } creation)
                return creation.Result;
            if (selectedCommand == RefreshDesktopCommand) return ShellContextMenuResult.RefreshRequested;
            if (selectedCommand == ToggleGroupsCommand) return ShellContextMenuResult.ToggleGroupsRequested;
            var isNewMenuCommand = IsCommandInNewSubmenu(contextMenu, menuHandle, selectedCommand);
            var commandOffset = selectedCommand - CommandFirst;
            var canonicalVerb = GetCanonicalVerb(contextMenu, commandOffset);
            if (canonicalVerb.Equals("paste", StringComparison.OrdinalIgnoreCase))
                return PasteToDesktop(screenPoint) == ShellContextMenuResult.CommandInvoked
                    ? ShellContextMenuResult.PasteInvoked
                    : ShellContextMenuResult.Failed;
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
        var result = contextMenu.InvokeCommand(ref invoke);
        if (result < 0)
            return Failure("InvokeCommand", result);
        return ShellContextMenuResult.CommandInvoked;
    }

    private const uint DesktopViewSmallCommand = 0x7101;
    private const uint DesktopViewMediumCommand = 0x7102;
    private const uint DesktopViewLargeCommand = 0x7103;
    private const uint DesktopSortNameCommand = 0x7110;
    private const uint DesktopSortSizeCommand = 0x7111;
    private const uint DesktopSortTypeCommand = 0x7112;
    private const uint DesktopSortDateCommand = 0x7113;
    private const uint DesktopSortAscendingCommand = 0x7114;
    private const uint DesktopSortDescendingCommand = 0x7115;
    private const uint DesktopGroupNoneCommand = 0x7120;
    private const uint DesktopGroupNameCommand = 0x7121;
    private const uint DesktopGroupSizeCommand = 0x7122;
    private const uint DesktopGroupTypeCommand = 0x7123;
    private const uint DesktopGroupDateCommand = 0x7124;
    private const uint DesktopGroupAscendingCommand = 0x7125;
    private const uint DesktopGroupDescendingCommand = 0x7126;
    private const uint DesktopAutoArrangeCommand = 0x7130;
    private const uint DesktopAlignGridCommand = 0x7131;

    private static void ReplaceDesktopLayoutMenus(IntPtr menuHandle, NativeDesktopState state)
    {
        ReplaceRootSubmenu(menuHandle, 0, [
            ("小图标", DesktopViewSmallCommand, state.ViewMode == "Small"),
            ("中等图标", DesktopViewMediumCommand, state.ViewMode is not "Small" and not "Large"),
            ("大图标", DesktopViewLargeCommand, state.ViewMode == "Large"),
            ("", 0u, false),
            ("自动排列", DesktopAutoArrangeCommand, state.AutoArrange),
            ("对齐到网格", DesktopAlignGridCommand, state.AlignToGrid)
        ]);
        ReplaceRootSubmenu(menuHandle, 1, [
            ("名称", DesktopSortNameCommand, state.SortMode == "Name"),
            ("大小", DesktopSortSizeCommand, state.SortMode == "Size"),
            ("项目类型", DesktopSortTypeCommand, state.SortMode == "Type"),
            ("修改日期", DesktopSortDateCommand, state.SortMode == "Date"),
            ("", 0u, false),
            ("递增", DesktopSortAscendingCommand, state.SortAscending),
            ("递减", DesktopSortDescendingCommand, !state.SortAscending)
        ]);
        ReplaceRootSubmenu(menuHandle, 2, [
            ("无", DesktopGroupNoneCommand, state.GroupMode == "None"),
            ("名称", DesktopGroupNameCommand, state.GroupMode == "Name"),
            ("大小", DesktopGroupSizeCommand, state.GroupMode == "Size"),
            ("项目类型", DesktopGroupTypeCommand, state.GroupMode == "Type"),
            ("修改日期", DesktopGroupDateCommand, state.GroupMode == "Date"),
            ("", 0u, false),
            ("递增", DesktopGroupAscendingCommand, state.GroupAscending),
            ("递减", DesktopGroupDescendingCommand, !state.GroupAscending)
        ]);
    }

    private static void RemoveDesktopLayoutMenus(IntPtr menuHandle)
    {
        // 收纳盒没有 Explorer 的图标视图可供这些命令操作，避免把操作转发给隐藏桌面层。
        for (var position = 2; position >= 0; position--)
        {
            var submenu = GetSubMenu(menuHandle, position);
            if (submenu == IntPtr.Zero) continue;
            _ = RemoveMenu(menuHandle, (uint)position, MfByPosition);
            _ = DestroyMenu(submenu);
        }
    }

    private static void ReplaceRootSubmenu(
        IntPtr root,
        int position,
        IReadOnlyList<(string Label, uint Command, bool Checked)> entries)
    {
        var existing = GetSubMenu(root, position);
        if (existing == IntPtr.Zero) return;
        _ = RemoveMenu(root, (uint)position, MfByPosition);
        _ = DestroyMenu(existing);

        var submenu = CreatePopupMenu();
        if (submenu == IntPtr.Zero) return;
        foreach (var entry in entries)
        {
            if (entry.Command == 0)
            {
                if (!AppendMenu(submenu, MfSeparator, IntPtr.Zero, string.Empty))
                {
                    DestroyMenu(submenu);
                    return;
                }
                continue;
            }
            var flags = MfString | (entry.Checked ? MfChecked : 0u);
            if (!AppendMenu(submenu, flags, new IntPtr(entry.Command), entry.Label))
            {
                DestroyMenu(submenu);
                return;
            }
        }
        if (!InsertMenu(root, (uint)position, MfByPosition | MfPopup, submenu,
                position switch { 0 => "查看", 1 => "排序方式", _ => "分组依据" }))
            DestroyMenu(submenu);
    }

    internal static bool TryMapDesktopLayoutCommand(
        uint command,
        out ShellContextMenuResult result)
    {
        result = command switch
        {
            DesktopViewSmallCommand => ShellContextMenuResult.DesktopViewSmall,
            DesktopViewMediumCommand => ShellContextMenuResult.DesktopViewMedium,
            DesktopViewLargeCommand => ShellContextMenuResult.DesktopViewLarge,
            DesktopSortNameCommand => ShellContextMenuResult.DesktopSortName,
            DesktopSortSizeCommand => ShellContextMenuResult.DesktopSortSize,
            DesktopSortTypeCommand => ShellContextMenuResult.DesktopSortType,
            DesktopSortDateCommand => ShellContextMenuResult.DesktopSortDate,
            DesktopSortAscendingCommand => ShellContextMenuResult.DesktopSortAscending,
            DesktopSortDescendingCommand => ShellContextMenuResult.DesktopSortDescending,
            DesktopGroupNoneCommand => ShellContextMenuResult.DesktopGroupNone,
            DesktopGroupNameCommand => ShellContextMenuResult.DesktopGroupName,
            DesktopGroupSizeCommand => ShellContextMenuResult.DesktopGroupSize,
            DesktopGroupTypeCommand => ShellContextMenuResult.DesktopGroupType,
            DesktopGroupDateCommand => ShellContextMenuResult.DesktopGroupDate,
            DesktopGroupAscendingCommand => ShellContextMenuResult.DesktopGroupAscending,
            DesktopGroupDescendingCommand => ShellContextMenuResult.DesktopGroupDescending,
            DesktopAutoArrangeCommand => ShellContextMenuResult.DesktopAutoArrange,
            DesktopAlignGridCommand => ShellContextMenuResult.DesktopAlignGrid,
            _ => ShellContextMenuResult.Failed
        };
        return result != ShellContextMenuResult.Failed;
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
            var result = contextMenu.InvokeCommand(ref invoke);
            if (result < 0)
                return Failure($"Invoke verb '{canonicalVerb}'", result);
            return ShellContextMenuResult.CommandInvoked;
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

    private static bool InsertDesktopRefreshCommand(IContextMenu contextMenu, IntPtr menuHandle)
    {
        // Explorer 的 Refresh 只刷新被隐藏的 SysListView32；移除它并把同一位置交给 MiniC。
        for (var index = GetMenuItemCount(menuHandle) - 1; index >= 0; index--)
        {
            var commandId = GetMenuItemId(menuHandle, index);
            if (commandId is >= CommandFirst and <= ShellCommandLast
                && GetCanonicalVerb(contextMenu, commandId - CommandFirst)
                    .Equals("refresh", StringComparison.OrdinalIgnoreCase))
                _ = RemoveMenu(menuHandle, (uint)index, MfByPosition);
        }

        return InsertMenu(menuHandle, 0, MfByPosition | MfString,
            new IntPtr(RefreshDesktopCommand), "刷新桌面");
    }

    internal static bool TryInsertMiniCDesktopCommands(
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
            foreach (var creation in BoxCreationCatalog.Options)
                if (!AppendMenu(miniCMenu, MfString, new IntPtr(creation.CommandId), creation.MenuLabel))
                    return false;
            if (!AppendMenu(miniCMenu, MfSeparator, IntPtr.Zero, string.Empty)
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
            var miniCPosition = FindSubMenuEntryPosition(menuHandle, BoxCreationCatalog.Options[0].CommandId);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveMenu(IntPtr menu, uint position, uint flags);

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
    PasteInvoked,
    RenameRequested,
    CreateGroupRequested,
    RefreshRequested,
    ToggleGroupsRequested,
    CreateWeatherRequested,
    CreatePlannerRequested,
    DesktopViewSmall,
    DesktopViewMedium,
    DesktopViewLarge,
    DesktopSortName,
    DesktopSortSize,
    DesktopSortType,
    DesktopSortDate,
    DesktopSortAscending,
    DesktopSortDescending,
    DesktopGroupNone,
    DesktopGroupName,
    DesktopGroupSize,
    DesktopGroupType,
    DesktopGroupDate,
    DesktopGroupAscending,
    DesktopGroupDescending,
    DesktopAutoArrange,
    DesktopAlignGrid
}
