using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace MiniC.Services;

/// <summary>通过 Explorer 桌面目标执行原生 OLE 剪贴板传输。</summary>
public static class ShellClipboardTransferService
{
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private static readonly Guid DropTargetId = new("00000122-0000-0000-C000-000000000046");

    public static bool PasteToDesktop(System.Drawing.Point screenPoint)
    {
        ComTypes.IDataObject? dataObject = null;
        IDropTarget? dropTarget = null;
        IShellFolder? desktopFolder = null;
        IntPtr targetPointer = IntPtr.Zero;
        try
        {
            if (OleGetClipboard(out dataObject) < 0 || dataObject is null) return false;
            var desktopHost = ExplorerDesktopWindowService.FindContentHost();
            if (desktopHost == 0 || SHGetDesktopFolder(out desktopFolder) < 0 || desktopFolder is null) return false;
            var dropTargetId = DropTargetId;
            if (desktopFolder.CreateViewObject(desktopHost, ref dropTargetId, out targetPointer) < 0
                || targetPointer == IntPtr.Zero) return false;
            dropTarget = (IDropTarget)Marshal.GetObjectForIUnknown(targetPointer);
            var effect = ReadClipboardDropEffect() == DropEffectMove ? DropEffectMove : DropEffectCopy;
            var point = new PointL(screenPoint.X, screenPoint.Y);
            if (dropTarget.DragEnter(dataObject, 0, point, ref effect) < 0 || effect == 0) return false;
            return dropTarget.Drop(dataObject, 0, point, ref effect) >= 0;
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(ShellClipboardTransferService), exception, "Desktop paste failed.");
            return false;
        }
        finally
        {
            if (dropTarget is not null && Marshal.IsComObject(dropTarget)) Marshal.FinalReleaseComObject(dropTarget);
            if (targetPointer != IntPtr.Zero) Marshal.Release(targetPointer);
            if (desktopFolder is not null && Marshal.IsComObject(desktopFolder)) Marshal.FinalReleaseComObject(desktopFolder);
            if (dataObject is not null && Marshal.IsComObject(dataObject)) Marshal.FinalReleaseComObject(dataObject);
        }
    }

    private static uint ReadClipboardDropEffect()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            return ClipboardItemStateService.ReadDropEffect(
                data?.GetData("Preferred DropEffect", autoConvert: false));
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(ShellClipboardTransferService), exception,
                "Unable to read Preferred DropEffect.");
            return DropEffectCopy;
        }
    }

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject dataObject,
            uint keyState, PointL point, ref uint effect);
        [PreserveSig] int DragOver(uint keyState, PointL point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject dataObject,
            uint keyState, PointL point, ref uint effect);
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
        [PreserveSig] int GetUIObjectOf(IntPtr window, uint count, IntPtr[] itemIdLists,
            ref Guid interfaceId, IntPtr reserved, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PointL(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard(
        [MarshalAs(UnmanagedType.Interface)] out ComTypes.IDataObject dataObject);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder desktopFolder);
}
