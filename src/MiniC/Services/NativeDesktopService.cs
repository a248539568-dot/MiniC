using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MiniC.ViewModels;

namespace MiniC.Services;

/// <summary>
/// 读取并定位 Windows Explorer 拥有的桌面图标，为 MiniC 显示层提供 Shell 桥接。
/// 服务不移动桌面文件、不修改文件属性，也不更改 Explorer 的排列设置。
/// </summary>
public sealed class NativeDesktopService
{
    private const uint ListViewFirst = 0x1000;
    private const uint GetItemCountMessage = ListViewFirst + 4;
    private const uint GetItemPositionMessage = ListViewFirst + 16;
    private const uint GetItemTextMessage = ListViewFirst + 115;
    private const uint SetItemPositionMessage = ListViewFirst + 49;
    private const uint ItemTextMask = 0x0001;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint MemoryCommit = 0x1000;
    private const uint MemoryReserve = 0x2000;
    private const uint MemoryRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint AbortIfHung = 0x0002;
    private const uint ShellDisplayName = 0x000000200;
    private const int MaximumTextLength = 520;
    private const uint MessageTimeoutMilliseconds = 800;
    private const int GwlStyle = -16;
    private const long LvsAutoArrange = 0x00000100;
    private readonly string[] _desktopDirectories;

    public NativeDesktopService()
    {
        _desktopDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        ];
    }

    /// <summary>确认 Explorer 桌面图标层可访问。</summary>
    public NativeDesktopCapability InspectCapability()
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
            return new(false, "没有找到 Windows 桌面图标层，请确认“显示桌面图标”已经开启。");

        try
        {
            _ = ReadIcons(listView);
            return new(true, "Windows 原生桌面图标可用。");
        }
        catch (Exception exception)
        {
            return new(false, $"无法访问 Windows 桌面图标：{exception.Message}");
        }
    }

    /// <summary>读取 Explorer 桌面图标的显示名称、序号和屏幕坐标。</summary>
    public IReadOnlyList<NativeDesktopIcon> ReadIcons()
    {
        var listView = FindDesktopListView();
        return listView == IntPtr.Zero ? [] : ReadIcons(listView);
    }

    /// <summary>返回当前 Explorer 桌面图标宿主标识；Explorer 重启后该值会变化。</summary>
    public nint GetDesktopHostIdentity() => FindDesktopListView();

    /// <summary>读取 Explorer 桌面当前的“自动排列图标”开关，不修改系统设置。</summary>
    public bool IsAutoArrangeEnabled()
    {
        var listView = FindDesktopListView();
        return listView != IntPtr.Zero && IsAutoArrangeStyle(GetWindowLongPtr(listView, GwlStyle).ToInt64());
    }

    internal static bool IsAutoArrangeStyle(long style) => (style & LvsAutoArrange) != 0;

    /// <summary>判断物理屏幕坐标当前是否由 Explorer 桌面图标视图承接。</summary>
    public bool IsDesktopSurfaceAt(int screenX, int screenY)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero) return false;
        var hit = WindowFromPoint(new NativePoint(screenX, screenY));
        var shellView = GetParent(listView);
        var desktopHost = shellView == IntPtr.Zero ? IntPtr.Zero : GetParent(shellView);
        return hit == listView || IsChild(listView, hit)
               || hit == shellView || (shellView != IntPtr.Zero && IsChild(shellView, hit))
               || hit == desktopHost;
    }

    /// <summary>显示或隐藏 Explorer 的整个桌面图标视图。</summary>
    public bool SetDesktopIconLayerVisible(bool visible)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero) return false;
        _ = ShowWindow(listView, visible ? 5 : 0);
        return true;
    }

    /// <summary>返回 Explorer 桌面图标视图当前是否可见。</summary>
    public bool IsDesktopIconLayerVisible()
    {
        var listView = FindDesktopListView();
        return listView != IntPtr.Zero && IsWindowVisible(listView);
    }

    /// <summary>批量设置真实桌面项目的原生图标坐标，并返回没有匹配到的路径。</summary>
    public IReadOnlyList<string> Arrange(IEnumerable<NativeDesktopPlacement> placements)
    {
        var requested = placements.ToArray();
        if (requested.Length == 0) return [];
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero) return requested.Select(item => item.Path).ToArray();

        var icons = ReadIcons(listView);
        var unmatched = new List<string>();
        foreach (var placement in requested)
        {
            var icon = MatchIcon(icons, placement.Path);
            if (icon is null || !TrySetPosition(listView, icon.Index, placement.ScreenX, placement.ScreenY))
                unmatched.Add(placement.Path);
        }
        return unmatched;
    }

    /// <summary>判断路径是否是用户桌面或公共桌面中的直接子项目。</summary>
    public bool IsDesktopItem(string path)
    {
        var normalized = NormalizePath(path);
        var parent = Path.GetDirectoryName(normalized);
        return parent is not null && _desktopDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(NormalizePath)
            .Contains(parent, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>枚举用户桌面与公共桌面的可见直接子项目，返回规范化完整路径。</summary>
    public IReadOnlyList<string> EnumerateDesktopItems()
    {
        var paths = new List<string>();
        foreach (var directory in _desktopDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    try
                    {
                        if (File.GetAttributes(path).HasFlag(FileAttributes.Hidden)) continue;
                        paths.Add(NormalizePath(path));
                    }
                    catch
                    {
                        // 项目可能在枚举期间被更新程序替换，下一轮会重新发现。
                    }
                }
            }
            catch
            {
                // 公共桌面可能暂时不可访问，不影响用户桌面继续工作。
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>读取指定收纳盒中仍存在于真实桌面的项目，用于标题计数和状态刷新。</summary>
    public Task<List<DesktopItem>> ReadAssignedItemsAsync(
        string groupId,
        IReadOnlyDictionary<string, string> assignments) => Task.Run(() =>
    {
        var items = new List<DesktopItem>();
        foreach (var (path, assignedGroupId) in assignments)
        {
            if (!assignedGroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var isDirectory = Directory.Exists(path);
                if (!isDirectory && !File.Exists(path)) continue;
                items.Add(new DesktopItem
                {
                    Path = path,
                    Name = GetDisplayName(path, isDirectory),
                    IsDirectory = isDirectory,
                    LastWriteTicks = isDirectory
                        ? Directory.GetLastWriteTimeUtc(path).Ticks
                        : File.GetLastWriteTimeUtc(path).Ticks,
                    GroupId = groupId
                });
            }
            catch
            {
                // 文件可能在枚举期间被更新程序原子替换，下一次桌面事件会重新读取。
            }
        }
        return items;
    });

    /// <summary>读取仍存在的真实桌面项目，不附加收纳盒归属。</summary>
    public Task<List<DesktopItem>> ReadItemsAsync(IEnumerable<string> paths) => Task.Run(() =>
    {
        var items = new List<DesktopItem>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var isDirectory = Directory.Exists(path);
                if (!isDirectory && !File.Exists(path)) continue;
                items.Add(new DesktopItem
                {
                    Path = path,
                    Name = GetDisplayName(path, isDirectory),
                    IsDirectory = isDirectory,
                    LastWriteTicks = isDirectory
                        ? Directory.GetLastWriteTimeUtc(path).Ticks
                        : File.GetLastWriteTimeUtc(path).Ticks
                });
            }
            catch
            {
                // 文件可能在刷新期间被替换，下一轮会重试。
            }
        }
        return items;
    });

    /// <summary>规范化用于持久化和比较的桌面路径。</summary>
    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>按 Explorer 当前设置返回文件名，包括系统的扩展名显示规则。</summary>
    public static string GetDisplayName(string path, bool isDirectory)
    {
        var shellName = GetShellDisplayName(path);
        if (!isDirectory && !DesktopFileService.AreFileExtensionsVisible())
            return Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(shellName)) return shellName;
        return isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
    }


    /// <summary>把真实路径与 Explorer 返回的显示名称关联起来。</summary>
    public NativeDesktopIcon? MatchIcon(IEnumerable<NativeDesktopIcon> icons, string path)
    {
        var candidates = GetDisplayNameCandidates(path);
        return icons.FirstOrDefault(icon => candidates.Contains(icon.DisplayName));
    }

    private static IReadOnlyList<NativeDesktopIcon> ReadIcons(IntPtr listView)
    {
        if (GetWindowThreadProcessId(listView, out var processId) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = new NativeProcessHandle(OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryLimitedInformation,
            false,
            processId));
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        var itemSize = Marshal.SizeOf<ListViewItem>();
        var textSize = MaximumTextLength * sizeof(char);
        var pointSize = Marshal.SizeOf<NativePoint>();
        var remoteMemory = VirtualAllocEx(process.Handle, IntPtr.Zero, (nuint)(itemSize + textSize + pointSize),
            MemoryCommit | MemoryReserve, PageReadWrite);
        if (remoteMemory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var remoteText = IntPtr.Add(remoteMemory, itemSize);
            var remotePoint = IntPtr.Add(remoteText, textSize);
            var count = SendListViewMessage(listView, GetItemCountMessage, IntPtr.Zero, IntPtr.Zero).ToInt32();
            var icons = new List<NativeDesktopIcon>(Math.Max(0, count));
            var textBuffer = new byte[textSize];
            var pointBuffer = new byte[pointSize];
            for (var index = 0; index < count; index++)
            {
                var item = new ListViewItem
                {
                    Mask = ItemTextMask,
                    Item = index,
                    Text = remoteText,
                    TextMax = MaximumTextLength
                };
                WriteRemoteStructure(process.Handle, remoteMemory, item);
                _ = SendListViewMessage(listView, GetItemTextMessage, new IntPtr(index), remoteMemory);
                if (!ReadProcessMemory(process.Handle, remoteText, textBuffer, textBuffer.Length, out _)) continue;
                var displayName = Encoding.Unicode.GetString(textBuffer);
                var terminator = displayName.IndexOf('\0');
                if (terminator >= 0) displayName = displayName[..terminator];

                if (SendListViewMessage(listView, GetItemPositionMessage, new IntPtr(index), remotePoint) == IntPtr.Zero)
                    continue;
                if (!ReadProcessMemory(process.Handle, remotePoint, pointBuffer, pointBuffer.Length, out _)) continue;
                var point = MemoryMarshal.Read<NativePoint>(pointBuffer);
                if (!ClientToScreen(listView, ref point)) continue;
                icons.Add(new NativeDesktopIcon(index, displayName, point.X, point.Y));
            }
            return icons;
        }
        finally
        {
            _ = VirtualFreeEx(process.Handle, remoteMemory, 0, MemoryRelease);
        }
    }

    private static bool TrySetPosition(IntPtr listView, int index, int screenX, int screenY)
    {
        var point = new NativePoint(screenX, screenY);
        if (!ScreenToClient(listView, ref point)) return false;
        if (GetWindowThreadProcessId(listView, out var processId) == 0) return false;
        using var process = new NativeProcessHandle(OpenProcess(
            ProcessVmOperation | ProcessVmWrite | ProcessQueryLimitedInformation, false, processId));
        if (process.IsInvalid) return false;
        var pointSize = Marshal.SizeOf<NativePoint>();
        var remotePoint = VirtualAllocEx(process.Handle, IntPtr.Zero, (nuint)pointSize,
            MemoryCommit | MemoryReserve, PageReadWrite);
        if (remotePoint == IntPtr.Zero) return false;
        try
        {
            WriteRemoteStructure(process.Handle, remotePoint, point);
            return SendListViewMessage(listView, SetItemPositionMessage, new IntPtr(index), remotePoint) != IntPtr.Zero;
        }
        finally
        {
            _ = VirtualFreeEx(process.Handle, remotePoint, 0, MemoryRelease);
        }
    }

    private static HashSet<string> GetDisplayNameCandidates(string path)
    {
        var fileName = Path.GetFileName(path);
        var candidates = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { fileName };
        var shellName = GetShellDisplayName(path);
        if (!string.IsNullOrWhiteSpace(shellName)) candidates.Add(shellName);
        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(extension)) candidates.Add(Path.GetFileNameWithoutExtension(fileName));
        return candidates;
    }

    private static string? GetShellDisplayName(string path)
    {
        var info = new ShellFileInfo();
        return SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShellDisplayName) == IntPtr.Zero
            ? null
            : info.DisplayName;
    }

    private static IntPtr FindDesktopListView()
        => ExplorerDesktopWindowService.FindListView();

    private static IntPtr SendListViewMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(window, message, wParam, lParam, AbortIfHung,
                MessageTimeoutMilliseconds, out var result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Explorer 桌面图标层没有及时响应。");
        return result;
    }

    private static void WriteRemoteStructure<T>(IntPtr process, IntPtr destination, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var local = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, local, false);
            var bytes = new byte[size];
            Marshal.Copy(local, bytes, 0, size);
            if (!WriteProcessMemory(process, destination, bytes, bytes.Length, out var written)
                || written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ListViewItem
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr Param;
        public int Indent;
        public int GroupId;
        public uint Columns;
        public IntPtr ColumnPointer;
        public IntPtr FormatPointer;
        public int Group;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    private sealed class NativeProcessHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public bool IsInvalid => Handle == IntPtr.Zero || Handle == new IntPtr(-1);
        public void Dispose()
        {
            if (!IsInvalid) _ = CloseHandle(Handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint infoSize, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out nuint written);
}

/// <summary>原生桌面能力检测结果。</summary>
public sealed record NativeDesktopCapability(bool IsAvailable, string Message);

/// <summary>Explorer 原生桌面图标快照。</summary>
public sealed record NativeDesktopIcon(int Index, string DisplayName, int ScreenX, int ScreenY);

/// <summary>要应用给真实桌面项目的屏幕像素坐标。</summary>
public sealed record NativeDesktopPlacement(string Path, int ScreenX, int ScreenY);
