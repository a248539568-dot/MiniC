using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace MiniC.Controllers;

/// <summary>后台协调独立收纳盒窗口、真实桌面文件和持久化状态。</summary>
public sealed class DesktopCoordinator : IDisposable
{
    private const int HotkeyId = 0xD35A;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint VkD = 0x44;
    private const int HotkeyMessage = 0x0312;
    private const int ClipboardUpdateMessage = 0x031D;
    private const int ShellChangeMessage = 0x8001;

    private readonly LayoutStore _layoutStore;
    private readonly DesktopFileService _desktopFileService;
    private readonly LegacyStorageMigrationService _migrationService;
    private readonly ShellContextMenuService _shellContextMenu;
    private readonly NativeDesktopService _nativeDesktopService;
    private readonly DesktopLayerHostService _desktopLayerHostService;
    private readonly DesktopDragStateService _desktopDragStateService = new();
    private readonly DesktopIconPlacementService _desktopIconPlacementService = new();
    private readonly DesktopAssignmentRetentionService _desktopAssignmentRetentionService = new();
    private readonly DesktopClipboardCoordinator _clipboardCoordinator = new();
    private readonly Action _shutdownApplication;
    private readonly Dictionary<string, DeskGroupWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _reconcileTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _renameGate = new(1, 1);
    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _applicationIcon;
    private readonly WinForms.ToolStripMenuItem _startupMenuItem;
    private readonly HwndSource _messageWindow;
    private TrayPanelWindow? _trayPanel;
    private DesktopSurfaceWindow? _desktopSurface;
    private LayoutState _layout = new();
    private bool _boxesVisible = true;
    private bool _initialDesktopPlacementCompleted;
    private bool _desktopBackgroundMenuActive;
    private readonly List<DesktopRename> _deferredDesktopRenames = [];
    private readonly Dictionary<string, string> _expectedRenames = new(StringComparer.OrdinalIgnoreCase);
    private long _suppressWatcherRefreshUntil;
    private bool _refreshDeferredByRename;
    private bool _renameInProgress;
    private bool _isExiting;
    private bool _isDisposed;
    private readonly RecycleBinIconService _recycleBinIcons = new();
    private DesktopDirectorySnapshot? _desktopSnapshot;
    private bool _refreshPending;
    private bool _reconciling;
    private bool _clipboardListenerRegistered;

    public ObservableCollection<DeskGroup> Groups { get; } = [];
    public ObservableCollection<DesktopItem> DesktopItems { get; } = [];

    public DesktopCoordinator(
        LayoutStore layoutStore,
        DesktopFileService desktopFileService,
        LegacyStorageMigrationService migrationService,
        ShellContextMenuService shellContextMenu,
        NativeDesktopService nativeDesktopService,
        DesktopLayerHostService desktopLayerHostService,
        Action shutdownApplication)
    {
        _layoutStore = layoutStore;
        _desktopFileService = desktopFileService;
        _migrationService = migrationService;
        _shellContextMenu = shellContextMenu;
        _nativeDesktopService = nativeDesktopService;
        _desktopLayerHostService = desktopLayerHostService;
        _shutdownApplication = shutdownApplication;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveLayoutAsync();
        };
        _reconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reconcileTimer.Tick += async (_, _) => await ReconcileAsync();

        _applicationIcon = Environment.ProcessPath is { } processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
              ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone()
            : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _trayIcon = new WinForms.NotifyIcon { Icon = _applicationIcon, Text = "MiniC", Visible = false };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                Application.Current.Dispatcher.InvokeAsync(ShowTrayPanel);
        };
        var trayMenu = new MiniC.Controls.CreamTrayMenu();
        trayMenu.Items.Add("新建收纳盒", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(AddGroup));
        trayMenu.Items.Add("显示 / 隐藏收纳盒", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ToggleBoxes));
        trayMenu.Items.Add("刷新", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(RefreshAsync));
        _startupMenuItem = new WinForms.ToolStripMenuItem("开机自动启动") { CheckOnClick = false };
        _startupMenuItem.Click += (_, _) => Application.Current.Dispatcher.InvokeAsync(ToggleStartup);
        trayMenu.Items.Add(_startupMenuItem);
        trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ExitAsync));
        _trayIcon.ContextMenuStrip = trayMenu;

        var parameters = new HwndSourceParameters("MiniCCoordinator")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3)
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(MessageWindowProc);
        _ = RegisterHotKey(_messageWindow.Handle, HotkeyId, ModControl | ModAlt, VkD);
        _clipboardListenerRegistered = AddClipboardFormatListener(_messageWindow.Handle);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemDisplaySettingsChanged;
    }

    private void SystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (_isExiting) return;
            _desktopSurface?.UpdateDisplayBounds();
            await RefreshAsync();
        });
    }

    public async Task InitializeAsync()
    {
        _ = _nativeDesktopService.SetDesktopIconLayerVisible(true);
        _layout = await _layoutStore.LoadAsync();
        _layout.StartWithWindows = StartupService.IsEnabled();
        _startupMenuItem.Checked = _layout.StartWithWindows.Value;
        LayoutRepairService.EnsureReferencedGroups(_layout,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth);
        SeparateOverlappingGroups(_layout.Groups);

        ShowDesktopSurface();

        foreach (var state in _layout.Groups)
        {
            var group = CreateGroupModel(state);
            Groups.Add(group);
        }
        NormalizeTabGroups();
        foreach (var group in Groups) ShowGroupWindow(group);
        EnsureGroupWindowCoverage();

        _desktopFileService.DesktopChanged += DesktopFileService_DesktopChanged;
        _desktopFileService.StartWatching(_messageWindow.Handle, ShellChangeMessage);
        _trayIcon.Visible = true;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        var migration = await _migrationService.MigrateAsync(_layout);
        await RefreshAsync();
        RefreshClipboardItemState();
        _ = _nativeDesktopService.SetDesktopIconLayerVisible(false);
        _reconcileTimer.Start();
        await SaveLayoutAsync();

        if (migration.Errors.Count > 0)
        {
            MessageBox.Show("部分旧版项目暂时无法迁回桌面，原文件仍保留在旧目录，下次启动会继续重试。\n\n"
                            + string.Join("\n", migration.Errors), "MiniC 旧数据迁移提醒",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowDesktopSurface()
    {
        if (_desktopSurface is not null) return;
        var callbacks = new DesktopSurfaceWindowCallbacks(
            ReleaseDesktopPathsAsync,
            DropExternalPathsOnDesktopAsync,
            DropOnItemAsync,
            (item, newName) => RenameItemCoreAsync(item, newName),
            ShowDesktopItemMenuAsync,
            () => ClearTransientState(_desktopSurface),
            ShowDesktopBackgroundMenuAsync);
        _desktopSurface = new DesktopSurfaceWindow(
            DesktopItems, callbacks, _desktopLayerHostService, _desktopDragStateService);
        _desktopSurface.Show();
    }

    public void ShowTrayPanel()
    {
        _trayPanel ??= new TrayPanelWindow(
            AddGroup,
            ApplyTheme,
            ApplyMaterial,
            ApplyOpacity,
            GetUnifiedStyle,
            ToggleBoxes);
        _trayPanel.ShowNearTray();
    }

    private DeskGroup CreateGroupModel(GroupState state)
    {
        var group = new DeskGroup
        {
            Id = state.Id,
            Name = state.Name,
            X = state.X,
            Y = state.Y,
            Width = Math.Max(250, state.Width),
            Height = Math.Max(170, state.Height),
            Theme = state.Theme,
            Material = state.Material,
            Opacity = state.Opacity is >= 0.22 and <= 0.92 ? state.Opacity : 0.62,
            ViewMode = state.ViewMode == "List" ? "List" : "Icons",
            ItemOrder = state.ItemOrder ?? [],
            IsCollapsed = state.IsCollapsed,
            TabGroupId = state.TabGroupId,
            TabOrder = state.TabOrder
        };
        group.PropertyChanged += (_, _) => ScheduleSave();
        return group;
    }

    private static void SeparateOverlappingGroups(IReadOnlyList<GroupState> groups)
    {
        var occupied = new HashSet<(int X, int Y)>();
        foreach (var cluster in groups.GroupBy(group => group.TabGroupId ?? group.Id, StringComparer.OrdinalIgnoreCase))
        {
            var members = cluster.OrderBy(group => group.TabOrder).ToArray();
            var x = (int)Math.Round(members[0].X);
            var y = (int)Math.Round(members[0].Y);
            while (!occupied.Add((x, y)))
            {
                x += 28;
                y += 28;
            }
            foreach (var group in members)
            {
                group.X = x;
                group.Y = y;
                group.Width = members[0].Width;
                group.Height = members[0].Height;
                group.IsCollapsed = members[0].IsCollapsed;
            }
        }
    }

    private void NormalizeTabGroups()
    {
        foreach (var cluster in Groups.Where(group => group.TabGroupId is not null)
                     .GroupBy(group => group.TabGroupId!, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var members = cluster.OrderBy(group => group.TabOrder).ToArray();
            if (members.Length < 2)
            {
                members[0].TabGroupId = null;
                members[0].TabOrder = 0;
                continue;
            }
            for (var index = 0; index < members.Length; index++)
            {
                members[index].TabOrder = index;
                members[index].X = members[0].X;
                members[index].Y = members[0].Y;
                members[index].Width = members[0].Width;
                members[index].Height = members[0].Height;
                members[index].IsCollapsed = members[0].IsCollapsed;
            }
        }
    }

    private void ShowGroupWindow(DeskGroup group)
    {
        var windowKey = GetWindowKey(group);
        if (_windows.ContainsKey(windowKey)) return;
        var members = GetTabMembers(group);
        var callbacks = new DeskGroupWindowCallbacks(
            _ => ScheduleSave(),
            CollectPathsAsync,
            DropOnItemAsync,
            ReleasePathsAsync,
            RenameItemAsync,
            ShowItemMenuAsync,
            activeGroup => ClearTransientState(FindWindowForGroup(activeGroup.Id)),
            active => _desktopSurface?.SetDropTargetActive(active),
            source => AddGroupNear(source),
            DissolveGroup,
            GetMergeTargets,
            FindMergeTargetAt,
            SetMergeDragTarget,
            MergeGroups,
            MergeTabIntoGroup,
            DetachGroup,
            _ => ScheduleSave());
        var window = new DeskGroupWindow(
            group, callbacks, _desktopLayerHostService, _desktopDragStateService);
        window.ConfigureTabs(members, group);
        _windows[windowKey] = window;
        window.Show();
        _desktopSurface?.RefreshDesktopPlacement();
    }

    private string GetWindowKey(DeskGroup group) => group.TabGroupId ?? group.Id;

    private DeskGroupWindow? FindWindowForGroup(string groupId) =>
        _windows.Values.FirstOrDefault(window => window.ContainsGroup(groupId));

    private DeskGroup[] GetTabMembers(DeskGroup group) => group.TabGroupId is null
        ? [group]
        : Groups.Where(candidate => candidate.TabGroupId?.Equals(
                group.TabGroupId, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(candidate => candidate.TabOrder).ToArray();

    private IReadOnlyList<DeskGroup> GetMergeTargets(DeskGroup source)
    {
        var sourceKey = GetWindowKey(source);
        return Groups.Where(group => !GetWindowKey(group).Equals(sourceKey, StringComparison.OrdinalIgnoreCase))
            .GroupBy(GetWindowKey, StringComparer.OrdinalIgnoreCase)
            .Select(cluster => cluster.OrderBy(group => group.TabOrder).First())
            .ToArray();
    }

    private void CloseWindowForGroup(DeskGroup group)
    {
        var entry = _windows.FirstOrDefault(pair => pair.Value.ContainsGroup(group.Id));
        if (entry.Value is null) return;
        _windows.Remove(entry.Key);
        entry.Value.ClosePermanently();
    }

    private void EnsureGroupWindowCoverage()
    {
        if (!_boxesVisible) return;
        var representatives = Groups
            .GroupBy(GetWindowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(cluster => cluster.Key,
                cluster => cluster.OrderBy(group => group.TabOrder).First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var staleKey in _windows.Keys.Where(key => !representatives.ContainsKey(key)).ToArray())
        {
            var staleWindow = _windows[staleKey];
            _windows.Remove(staleKey);
            staleWindow.ClosePermanently();
        }

        foreach (var (windowKey, representative) in representatives)
        {
            if (!_windows.TryGetValue(windowKey, out var window))
            {
                ShowGroupWindow(representative);
                continue;
            }
            var expectedMembers = GetTabMembers(representative);
            if (!window.Members.Select(member => member.Id)
                    .SequenceEqual(expectedMembers.Select(member => member.Id), StringComparer.OrdinalIgnoreCase))
            {
                var activeGroup = expectedMembers.Contains(window.Group) ? window.Group : representative;
                window.ConfigureTabs(expectedMembers, activeGroup);
            }
            if (!window.IsVisible) window.Show();
            window.RefreshDesktopPlacement();
        }
    }

    private void AddGroup() => AddGroupNear(Groups.LastOrDefault());

    private void AddGroupNear(DeskGroup? source)
    {
        var style = GetUnifiedStyle();
        var index = Groups.Count + 1;
        var group = new DeskGroup
        {
            Name = $"收纳盒 {index}",
            X = source is null ? 70 + index % 8 * 26 : source.X + 28,
            Y = source is null ? 130 + index % 8 * 26 : source.Y + 28,
            Width = source?.Width ?? 380,
            Height = source?.Height ?? 320,
            Theme = style.Theme,
            Material = style.Material,
            Opacity = style.Opacity
        };
        group.PropertyChanged += (_, _) => ScheduleSave();
        Groups.Add(group);
        ShowGroupWindow(group);
        ScheduleSave();
    }

    private async Task CollectPathsAsync(DeskGroup target, IReadOnlyList<string> paths, bool copyExternal)
    {
        var desktopPaths = new List<string>();
        var externalPaths = new List<string>();
        foreach (var value in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = NativeDesktopService.NormalizePath(value);
                if ((File.Exists(path) || Directory.Exists(path)) && _nativeDesktopService.IsDesktopItem(path))
                    desktopPaths.Add(path);
                else
                    externalPaths.Add(value);
            }
            catch
            {
                externalPaths.Add(value);
            }
        }

        var errors = new List<string>();
        if (externalPaths.Count > 0)
        {
            var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var result = await _desktopFileService.DropIntoFolderAsync(externalPaths, desktopDirectory, copyExternal);
            desktopPaths.AddRange(result.DestinationPaths);
            errors.AddRange(result.Errors);
        }

        CaptureOriginalPositions(desktopPaths);
        foreach (var path in desktopPaths) _layout.NativeDesktop.Assignments[path] = target.Id;
        await RefreshAsync();
        ScheduleSave();
        if (errors.Count > 0)
            MessageBox.Show(string.Join("\n", errors), "部分项目未能收纳", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task DropExternalPathsOnDesktopAsync(
        IReadOnlyList<string> paths,
        System.Windows.Point screenPoint,
        System.Windows.DragDropEffects effect)
    {
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var result = await _desktopFileService.DropIntoFolderAsync(
            paths.Where(path => !path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)),
            desktopDirectory,
            effect == System.Windows.DragDropEffects.Copy);
        var placedPaths = result.DestinationPaths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        _desktopIconPlacementService.PlaceAt(
            placedPaths, screenPoint, DesktopItems, _layout.NativeDesktop.ManagedPositions);
        await RefreshAsync();
        ScheduleSave();
        ShowFileOperationErrors(result.Errors);
    }

    private async Task DropOnItemAsync(
        DesktopItem target,
        IReadOnlyList<string> paths,
        bool copy)
    {
        var realPaths = paths.Where(path => !path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
        if (realPaths.Length == 0) return;

        List<string> changedSources;
        List<string> errors;
        if (target.IsVirtual && target.Path.Equals(
                DesktopFileService.RecycleBinShellPath, StringComparison.OrdinalIgnoreCase))
        {
            var result = await _desktopFileService.MoveToRecycleBinAsync(realPaths);
            changedSources = result.RemovedPaths;
            errors = result.Errors;
        }
        else if (!target.IsVirtual && Directory.Exists(target.Path))
        {
            var result = await _desktopFileService.DropIntoFolderAsync(realPaths, target.Path, copy);
            changedSources = result.MovedPaths;
            errors = result.Errors;
        }
        else return;

        foreach (var path in changedSources)
        {
            _layout.NativeDesktop.Assignments.Remove(path);
            _layout.NativeDesktop.ManagedPositions.Remove(path);
        }
        await RefreshAsync();
        ScheduleSave();
        ShowFileOperationErrors(errors);
    }

    private static void ShowFileOperationErrors(IReadOnlyCollection<string> errors)
    {
        if (errors.Count > 0)
            MessageBox.Show(string.Join("\n", errors), "部分项目未能移动",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task ReleasePathsAsync(
        DeskGroup source,
        IReadOnlyList<string> paths,
        System.Windows.Point screenPoint,
        System.Windows.DragDropEffects effect)
    {
        var target = FindGroupAt(screenPoint);
        if (target is not null)
        {
            if (!ReferenceEquals(target, source))
            {
                foreach (var path in paths) _layout.NativeDesktop.Assignments[path] = target.Id;
                await RefreshAsync();
                ScheduleSave();
            }
            return;
        }
        if (effect == System.Windows.DragDropEffects.Copy || !IsDesktopAt(screenPoint))
            return;

        foreach (var path in paths) _layout.NativeDesktop.Assignments.Remove(path);
        var existingPaths = paths.Where(path => path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)
                                                || File.Exists(path) || Directory.Exists(path)).ToArray();
        _desktopIconPlacementService.PlaceAt(
            existingPaths, screenPoint, DesktopItems, _layout.NativeDesktop.ManagedPositions);
        await RefreshAsync();
        ScheduleSave();
    }

    private async Task ReleaseDesktopPathsAsync(
        IReadOnlyList<string> paths,
        System.Windows.Point screenPoint,
        System.Windows.DragDropEffects effect)
    {
        var target = FindGroupAt(screenPoint);
        if (target is not null)
        {
            var realDesktopPaths = paths.Where(path =>
                    !path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(path) || Directory.Exists(path))
                    && _nativeDesktopService.IsDesktopItem(path))
                .ToArray();
            if (realDesktopPaths.Length > 0)
            {
                foreach (var path in realDesktopPaths) _layout.NativeDesktop.Assignments[path] = target.Id;
                await RefreshAsync();
                ScheduleSave();
            }
            return;
        }

        if (effect == System.Windows.DragDropEffects.Copy || !IsDesktopAt(screenPoint)) return;
        var existingPaths = paths.Where(path => path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)
                                                || File.Exists(path) || Directory.Exists(path)).ToArray();
        _desktopIconPlacementService.PlaceAt(
            existingPaths, screenPoint, DesktopItems, _layout.NativeDesktop.ManagedPositions);
        await RefreshAsync();
        ScheduleSave();
    }

    private async Task<bool> RenameItemAsync(DeskGroup group, DesktopItem item, string newName)
        => await RenameItemCoreAsync(item, newName);

    private async Task<bool> RenameItemCoreAsync(DesktopItem item, string newName)
    {
        if (_isExiting) return false;
        await _renameGate.WaitAsync();
        if (_isExiting) { _renameGate.Release(); return false; }
        _renameInProgress = true;
        await _refreshGate.WaitAsync();
        try
        {
            var oldPath = item.Path;
            var result = await _desktopFileService.RenameItemAsync(oldPath, newName);
            if (!result.Succeeded || result.NewPath is null)
            {
                MessageBox.Show(result.Error ?? "无法重命名该项目。", "重命名失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (_layout.NativeDesktop.Assignments.Remove(oldPath, out var groupId))
                _layout.NativeDesktop.Assignments[result.NewPath] = groupId;
            if (_layout.NativeDesktop.ManagedPositions.Remove(oldPath, out var position))
                _layout.NativeDesktop.ManagedPositions[result.NewPath] = position;
            _expectedRenames[oldPath] = result.NewPath;
            _suppressWatcherRefreshUntil = Environment.TickCount64 + 900;
            item.Path = result.NewPath;
            item.Name = NativeDesktopService.GetDisplayName(result.NewPath, item.IsDirectory);
            foreach (var group in Groups)
            {
                var orderIndex = group.ItemOrder.FindIndex(name => name.Equals(Path.GetFileName(oldPath), StringComparison.OrdinalIgnoreCase));
                if (orderIndex >= 0) group.ItemOrder[orderIndex] = Path.GetFileName(result.NewPath);
            }
            ScheduleSave();
            return true;
        }
        finally
        {
            _renameInProgress = false;
            _refreshGate.Release();
            _renameGate.Release();
        }
    }

    private async Task ShowItemMenuAsync(
        DeskGroup group,
        IReadOnlyList<DesktopItem> selectedItems,
        DesktopItem clickedItem,
        System.Drawing.Point screenPoint)
    {
        var window = FindWindowForGroup(group.Id);
        if (window is null) return;
        await ShowSystemItemMenuAsync(window, selectedItems, clickedItem, screenPoint,
            item => window.BeginRename(item));
    }

    private async Task ShowDesktopItemMenuAsync(
        IReadOnlyList<DesktopItem> selectedItems,
        DesktopItem clickedItem,
        System.Drawing.Point screenPoint)
    {
        if (_desktopSurface is null) return;
        await ShowSystemItemMenuAsync(_desktopSurface, selectedItems, clickedItem, screenPoint,
            item => _desktopSurface.BeginRename(item));
    }

    private async Task ShowSystemItemMenuAsync(
        Window sourceWindow,
        IReadOnlyList<DesktopItem> selectedItems,
        DesktopItem clickedItem,
        System.Drawing.Point screenPoint,
        Action<DesktopItem> beginRename)
    {
        SetTransientPopupState(sourceWindow, true);
        var ownerWindow = new ShellMenuOwnerWindow(screenPoint);
        ShellContextMenuResult result;
        try
        {
            var owner = ownerWindow.ShowAndGetHandle();
            result = _shellContextMenu.Show(owner, selectedItems.Select(item => item.Path), screenPoint);
        }
        finally
        {
            ownerWindow.Close();
            SetTransientPopupState(sourceWindow, false);
        }

        if (result == ShellContextMenuResult.RenameRequested)
        {
            beginRename(clickedItem);
            return;
        }
        if (result is ShellContextMenuResult.CopyInvoked or ShellContextMenuResult.CutInvoked)
        {
            SetClipboardItemState(
                selectedItems.Select(item => item.Path),
                result == ShellContextMenuResult.CutInvoked
                    ? ClipboardTransferState.Cut
                    : ClipboardTransferState.Copied);
        }
        else if (result == ShellContextMenuResult.CommandInvoked)
        {
            await Task.Delay(160);
            await RefreshAsync();
        }
        ClearTransientState();
    }

    private void ClearTransientState(Window? except = null)
    {
        if (!ReferenceEquals(_desktopSurface, except)) _desktopSurface?.ClearTransientState();
        foreach (var window in _windows.Values)
            if (!ReferenceEquals(window, except)) window.ClearTransientState();
    }

    private void RefreshClipboardItemState()
    {
        if (_isExiting) return;
        _clipboardCoordinator.Refresh(DesktopItems, Groups.SelectMany(group => group.Items));
    }

    private void SetClipboardItemState(IEnumerable<string> paths, ClipboardTransferState state)
    {
        _clipboardCoordinator.Set(paths, state,
            DesktopItems, Groups.SelectMany(group => group.Items));
    }

    private void ApplyClipboardItemState()
    {
        _clipboardCoordinator.Apply(DesktopItems, Groups.SelectMany(group => group.Items));
    }

    private static void SetTransientPopupState(Window window, bool open)
    {
        if (window is DesktopSurfaceWindow desktop) desktop.SetTransientPopupOpen(open);
        else if (window is DeskGroupWindow group) group.SetTransientPopupOpen(open);
    }

    private void CaptureOriginalPositions(IEnumerable<string> paths)
    {
        var icons = _nativeDesktopService.ReadIcons();
        foreach (var path in paths)
        {
            if (_layout.NativeDesktop.ManagedPositions.ContainsKey(path)) continue;
            var icon = _nativeDesktopService.MatchIcon(icons, path);
            if (icon is null) continue;
            _layout.NativeDesktop.ManagedPositions[path] = new DesktopIconPositionState
                { X = icon.ScreenX, Y = icon.ScreenY };
        }
    }

    private async Task RefreshAsync()
    {
        if (_isExiting) return;
        _refreshPending = true;
        if (_renameInProgress || IsRenameEditing())
        {
            _refreshDeferredByRename = true;
            return;
        }
        if (_desktopBackgroundMenuActive || !await _refreshGate.WaitAsync(0)) return;
        try
        {
            do
            {
                _refreshPending = false;
                await RefreshCoreAsync();
            } while (_refreshPending && !_isExiting && !_desktopBackgroundMenuActive
                     && !_renameInProgress && !IsRenameEditing());
        }
        catch
        {
            _refreshPending = true;
            throw;
        }
        finally { _refreshGate.Release(); }
    }

    private Task<DesktopDirectorySnapshot> CaptureDesktopSnapshotAsync() => Task.Run(() =>
        DesktopDirectorySnapshot.Capture(_nativeDesktopService.EnumerateDesktopItems()));

    private async Task RefreshCoreAsync()
    {
        ApplyRenames(_deferredDesktopRenames.ToArray());
        _deferredDesktopRenames.Clear();
        var snapshot = await CaptureDesktopSnapshotAsync();
        if (_isExiting) return;
        var existingPaths = snapshot.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _desktopAssignmentRetentionService.Reconcile(
            _layout.NativeDesktop.Assignments, existingPaths, Environment.TickCount64);

        var groupDiscoveries = await Task.WhenAll(Groups.Select(async group =>
            (Group: group, Items: await _nativeDesktopService.ReadAssignedItemsAsync(
                group.Id, _layout.NativeDesktop.Assignments))));
        if (_isExiting) return;
        foreach (var (group, discoveredItems) in groupDiscoveries)
        {
            var discovered = discoveredItems;
            var order = group.ItemOrder.Select((name, index) => (name, index))
                .GroupBy(pair => pair.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(items => items.Key, items => items.First().index, StringComparer.OrdinalIgnoreCase);
            discovered = discovered
                .OrderBy(item => order.TryGetValue(Path.GetFileName(item.Path), out var index) ? index : int.MaxValue)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var existing = group.Items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            var desired = discovered.Select(item =>
            {
                if (!existing.TryGetValue(item.Path, out var current)) return item;
                return DesktopItemRefreshService.Merge(current, item);
            }).ToArray();
            SynchronizeItems(group.Items, desired);
            group.ItemOrder = group.Items.Select(item => Path.GetFileName(item.Path)).ToList();
            _ = LoadIconsAsync(group.Items, group.Items.Where(item => item.Icon is null || item.NeedsIconRefresh).ToArray());
        }

        var unassignedPaths = existingPaths
            .Where(path => !_layout.NativeDesktop.Assignments.ContainsKey(path))
            .ToArray();
        var desktopDiscovered = await _nativeDesktopService.ReadItemsAsync(unassignedPaths);
        if (_isExiting) return;
        desktopDiscovered.AddRange(DesktopFileService.GetVisibleVirtualDesktopItems());
        EnsureDesktopPositions(desktopDiscovered, useExplorerPositions: !_initialDesktopPlacementCompleted);
        _initialDesktopPlacementCompleted = true;
        if (_nativeDesktopService.IsAutoArrangeEnabled()
            && _desktopIconPlacementService.AutoArrange(
                desktopDiscovered, _layout.NativeDesktop.ManagedPositions))
            ScheduleSave();
        desktopDiscovered = desktopDiscovered
            .OrderBy(item => _layout.NativeDesktop.ManagedPositions[item.Path].X)
            .ThenBy(item => _layout.NativeDesktop.ManagedPositions[item.Path].Y)
            .ToList();
        var existingDesktop = DesktopItems.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var desiredDesktop = desktopDiscovered.Select(item =>
        {
            if (!existingDesktop.TryGetValue(item.Path, out var current)) return item;
            return DesktopItemRefreshService.Merge(current, item);
        }).ToArray();
        SynchronizeItems(DesktopItems, desiredDesktop);
        foreach (var item in DesktopItems)
        {
            var position = _layout.NativeDesktop.ManagedPositions[item.Path];
            _desktopSurface?.SetScreenPosition(item, position.X, position.Y);
        }
        _ = LoadIconsAsync(DesktopItems, DesktopItems.Where(item => item.Icon is null || item.NeedsIconRefresh).ToArray());
        await _recycleBinIcons.RefreshAsync(DesktopItems.Where(RecycleBinIconService.IsRecycleBin).ToArray());
        if (_isExiting) return;
        ApplyClipboardItemState();
        _desktopSnapshot = snapshot;
    }

    private void EnsureDesktopPositions(IReadOnlyList<DesktopItem> items, bool useExplorerPositions)
    {
        var missingItems = items
            .Where(item => !_layout.NativeDesktop.ManagedPositions.ContainsKey(item.Path)).ToArray();

        var preferredPositions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase);
        if (useExplorerPositions)
        {
            var icons = _nativeDesktopService.ReadIcons();
            foreach (var item in missingItems)
            {
                var icon = item.IsVirtual
                    ? icons.FirstOrDefault(candidate => candidate.DisplayName.Equals(
                        item.Name, StringComparison.CurrentCultureIgnoreCase))
                    : _nativeDesktopService.MatchIcon(icons, item.Path);
                if (icon is not null)
                    preferredPositions[item.Path] = new System.Windows.Point(icon.ScreenX, icon.ScreenY);
            }
        }

        if (_desktopIconPlacementService.PlaceMissing(
            items, _layout.NativeDesktop.ManagedPositions, preferredPositions, useExplorerPositions))
            ScheduleSave();
    }

    private static void SynchronizeItems(
        ObservableCollection<DesktopItem> target,
        IReadOnlyList<DesktopItem> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], desired[index])) continue;
            var existingIndex = target.IndexOf(desired[index]);
            if (existingIndex >= 0) target.Move(existingIndex, index);
            else target.Insert(index, desired[index]);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    private static async Task LoadIconsAsync(
        ObservableCollection<DesktopItem> target,
        IReadOnlyCollection<DesktopItem> items)
    {
        await Parallel.ForEachAsync(items.Where(item => !RecycleBinIconService.IsRecycleBin(item)),
            new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (item, _) =>
        {
            var path = item.Path;
            var revision = item.LastWriteTicks;
            var icon = await Task.Run(() => ShellIconService.GetIcon(path, item.IsDirectory));
            if (icon is null) return;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (target.Contains(item) && item.Path == path && item.LastWriteTicks == revision)
                {
                    item.Icon = icon;
                    item.NeedsIconRefresh = false;
                }
            });
        });
    }

    private void DesktopFileService_DesktopChanged(object? sender, DesktopChangedEventArgs args)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (_isExiting) return;
            var externalRenames = args.Renames.Where(rename =>
            {
                var oldPath = NativeDesktopService.NormalizePath(rename.OldPath);
                var newPath = NativeDesktopService.NormalizePath(rename.NewPath);
                return !_expectedRenames.Remove(oldPath, out var expectedPath)
                       || !expectedPath.Equals(newPath, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (args.Renames.Count > 0 && externalRenames.Length == 0 && !args.HasFileChanges) return;
            // 外部改名统一在刷新锁内应用，避免后台枚举 Assignments 时修改同一字典。
            _deferredDesktopRenames.AddRange(externalRenames);
            if (_renameInProgress || IsRenameEditing()
                || Environment.TickCount64 <= _suppressWatcherRefreshUntil)
            {
                _refreshDeferredByRename = true;
                return;
            }
            if (_desktopBackgroundMenuActive)
            {
                _refreshPending = true;
                return;
            }
            await RefreshAsync();
            _desktopSurface?.RefreshDesktopPlacement();
            foreach (var window in _windows.Values.Where(window => window.IsVisible))
                window.RefreshDesktopPlacement();
            ScheduleSave();
        });
    }

    private async Task ShowDesktopBackgroundMenuAsync(System.Drawing.Point screenPoint)
    {
        if (_desktopSurface is null || _desktopBackgroundMenuActive) return;
        _desktopBackgroundMenuActive = true;
        await _refreshGate.WaitAsync();
        _refreshGate.Release();
        var pathsBeforeCommand = _nativeDesktopService.EnumerateDesktopItems()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = ShellContextMenuResult.Failed;
        IReadOnlyList<string> newItemPaths = [];
        var ownerWindow = new ShellMenuOwnerWindow(screenPoint);
        SetTransientPopupState(_desktopSurface, true);
        try
        {
            var owner = ownerWindow.ShowAndGetHandle();
            result = _shellContextMenu.ShowDesktopBackground(owner, screenPoint);
            if (result == ShellContextMenuResult.NewItemCreated)
                newItemPaths = await WaitForDesktopCommandToSettleAsync(pathsBeforeCommand);
        }
        finally
        {
            ownerWindow.Close();
            SetTransientPopupState(_desktopSurface, false);
            _desktopBackgroundMenuActive = false;
        }

        var deferredRenames = _deferredDesktopRenames.ToArray();
        _deferredDesktopRenames.Clear();
        ApplyRenames(deferredRenames);
        switch (result)
        {
            case ShellContextMenuResult.CreateGroupRequested:
                AddGroup();
                break;
            case ShellContextMenuResult.RefreshRequested:
                await RefreshAsync();
                ScheduleSave();
                break;
            case ShellContextMenuResult.ToggleGroupsRequested:
                ToggleBoxes();
                break;
            case ShellContextMenuResult.CommandInvoked:
                await RefreshAsync();
                ScheduleSave();
                break;
            case ShellContextMenuResult.NewItemCreated:
                await RefreshAsync();
                PlaceCreatedItemsAtPointer(newItemPaths, screenPoint);
                ScheduleSave();
                break;
            default:
                if (deferredRenames.Length > 0)
                {
                    await RefreshAsync();
                    ScheduleSave();
                }
                break;
        }
        ClearTransientState();
        if (result == ShellContextMenuResult.NewItemCreated)
            BeginRenameCreatedItem(newItemPaths);
    }

    private async Task<IReadOnlyList<string>> WaitForDesktopCommandToSettleAsync(IReadOnlySet<string> pathsBeforeCommand)
    {
        HashSet<string>? previousSnapshot = null;
        var changeObserved = false;
        var stableChecks = 0;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var snapshot = _nativeDesktopService.EnumerateDesktopItems()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            changeObserved |= !snapshot.SetEquals(pathsBeforeCommand);
            if (changeObserved && previousSnapshot is not null && snapshot.SetEquals(previousSnapshot))
                stableChecks++;
            else
                stableChecks = 0;
            if (stableChecks >= 3)
                return snapshot.Except(pathsBeforeCommand, StringComparer.OrdinalIgnoreCase).ToArray();
            previousSnapshot = snapshot;
            await Task.Delay(100);
        }
        return _nativeDesktopService.EnumerateDesktopItems()
            .Except(pathsBeforeCommand, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void BeginRenameCreatedItem(IReadOnlyList<string> paths)
    {
        var path = paths.Count == 1 ? paths[0] : paths.LastOrDefault();
        if (string.IsNullOrWhiteSpace(path)) return;
        var item = DesktopItems.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is not null) _desktopSurface?.BeginRename(item);
    }

    private void PlaceCreatedItemsAtPointer(
        IReadOnlyList<string> paths,
        System.Drawing.Point screenPoint)
    {
        var createdPaths = paths
            .Where(path => DesktopItems.Any(item =>
                item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (createdPaths.Length == 0) return;

        _desktopIconPlacementService.PlaceAt(
            createdPaths,
            new System.Windows.Point(screenPoint.X, screenPoint.Y),
            DesktopItems,
            _layout.NativeDesktop.ManagedPositions);
        foreach (var path in createdPaths)
        {
            var item = DesktopItems.First(candidate =>
                candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            var position = _layout.NativeDesktop.ManagedPositions[path];
            _desktopSurface?.SetScreenPosition(item, position.X, position.Y);
        }
    }

    private void ApplyRenames(IEnumerable<DesktopRename> renames)
    {
        foreach (var rename in renames)
        {
            var oldPath = NativeDesktopService.NormalizePath(rename.OldPath);
            var newPath = NativeDesktopService.NormalizePath(rename.NewPath);
            if (_layout.NativeDesktop.Assignments.Remove(oldPath, out var groupId)
                && _nativeDesktopService.IsDesktopItem(newPath))
                _layout.NativeDesktop.Assignments[newPath] = groupId;
            if (_layout.NativeDesktop.ManagedPositions.Remove(oldPath, out var position))
                _layout.NativeDesktop.ManagedPositions[newPath] = position;
        }
    }

    private async Task ReconcileAsync()
    {
        if (_isExiting || _desktopBackgroundMenuActive || _reconciling) return;
        _reconciling = true;
        try
        {
            EnsureGroupWindowCoverage();
            _desktopSurface?.RefreshDesktopPlacement();
            foreach (var window in _windows.Values.Where(window => window.IsVisible))
                window.RefreshDesktopPlacement();
            _ = _recycleBinIcons.RefreshAsync(DesktopItems.Where(RecycleBinIconService.IsRecycleBin).ToArray());
            if (_renameInProgress || IsRenameEditing()
                || Environment.TickCount64 <= _suppressWatcherRefreshUntil) return;
            if (_refreshDeferredByRename || _refreshPending)
            {
                _refreshDeferredByRename = false;
                await RefreshAsync();
                if (!_isExiting) _ = _nativeDesktopService.SetDesktopIconLayerVisible(false);
                return;
            }
            var snapshot = await CaptureDesktopSnapshotAsync();
            if (_isExiting) return;
            if (!snapshot.Matches(_desktopSnapshot))
            {
                await RefreshAsync();
                if (!_isExiting) _ = _nativeDesktopService.SetDesktopIconLayerVisible(false);
                return;
            }
            _ = _nativeDesktopService.SetDesktopIconLayerVisible(false);
        }
        finally { _reconciling = false; }
    }

    private bool IsRenameEditing() => DesktopItems.Any(item => item.IsRenaming)
                                      || Groups.SelectMany(group => group.Items).Any(item => item.IsRenaming)
                                      || Groups.Any(group => group.IsRenaming);

    private DeskGroup? FindGroupAt(System.Windows.Point screenPoint) =>
        _windows.Where(entry => entry.Value.IsVisible)
            .Select(entry =>
            {
                var topLeft = entry.Value.PointToScreen(new System.Windows.Point());
                var bottomRight = entry.Value.PointToScreen(
                    new System.Windows.Point(entry.Value.ActualWidth, entry.Value.ActualHeight));
                return (Bounds: new Rect(topLeft, bottomRight),
                    Group: entry.Value.Group);
            })
            .LastOrDefault(entry => entry.Bounds.Contains(screenPoint)).Group;

    private DeskGroup? FindMergeTargetAt(DeskGroup source, System.Windows.Point screenPoint)
    {
        var sourceKey = GetWindowKey(source);
        var candidates = _windows
            .Where(entry => entry.Value.IsVisible
                            && !entry.Key.Equals(sourceKey, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (Bounds: entry.Value.GetMergeDropScreenBounds(), Group: entry.Value.Group))
            .Where(entry => !entry.Bounds.IsEmpty && entry.Bounds.Contains(screenPoint))
            .ToArray();
        return SelectNearestMergeTarget(candidates, screenPoint);
    }

    private static DeskGroup? SelectNearestMergeTarget(
        IReadOnlyCollection<(Rect Bounds, DeskGroup Group)> candidates,
        System.Windows.Point screenPoint)
    {
        if (candidates.Count == 0) return null;
        return candidates.MinBy(entry =>
            {
                var deltaX = entry.Bounds.Left + entry.Bounds.Width / 2 - screenPoint.X;
                var deltaY = entry.Bounds.Top + entry.Bounds.Height / 2 - screenPoint.Y;
                return deltaX * deltaX + deltaY * deltaY;
            }).Group;
    }

    internal static bool ValidateEmptyMergeTargetForSmokeTest() =>
        SelectNearestMergeTarget([], new System.Windows.Point()) is null;

    private void SetMergeDragTarget(DeskGroup source, DeskGroup? target)
    {
        var sourceWindow = FindWindowForGroup(source.Id);
        foreach (var window in _windows.Values)
            window.SetMergeDropTarget(target is not null && window.ContainsGroup(target.Id));
        var targetWindow = target is null ? null : FindWindowForGroup(target.Id);
        sourceWindow?.SetMergeSourceHover(targetWindow is null ? null
            : new Rect(targetWindow.Left, targetWindow.Top, targetWindow.Width, targetWindow.Height));
    }

    private bool IsDesktopAt(System.Windows.Point screenPoint) =>
        _desktopSurface?.IsWindowAt((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)) == true
        || _nativeDesktopService.IsDesktopSurfaceAt(
            (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));

    private void DissolveGroup(DeskGroup group)
    {
        if (MessageBox.Show($"解散“{group.Name}”？\n文件仍保留在桌面，只会移除视觉归属。", "MiniC",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var paths = _layout.NativeDesktop.Assignments
            .Where(entry => entry.Value.Equals(group.Id, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key).ToArray();
        foreach (var path in paths) _layout.NativeDesktop.Assignments.Remove(path);
        var remainingMembers = GetTabMembers(group).Where(member => !ReferenceEquals(member, group)).ToArray();
        CloseWindowForGroup(group);
        Groups.Remove(group);
        NormalizeDetachedCluster(remainingMembers);
        if (remainingMembers.Length > 0) ShowGroupWindow(remainingMembers[0]);
        ScheduleSave();
    }

    private void MergeGroups(DeskGroup source, DeskGroup target)
    {
        if (GetWindowKey(source).Equals(GetWindowKey(target), StringComparison.OrdinalIgnoreCase)) return;
        var sourceMembers = GetTabMembers(source);
        var targetMembers = GetTabMembers(target);
        var clusterId = target.TabGroupId ?? Guid.NewGuid().ToString("N");
        var combined = targetMembers.Concat(sourceMembers).Distinct().ToArray();
        var sourcePosition = new System.Windows.Point(source.X, source.Y);
        ApplyGroupTopologyChange(() =>
        {
            for (var index = 0; index < combined.Length; index++)
                ApplyClusterState(combined[index], target, clusterId, index);
        }, source, window => window.PlayMergeCompletedAnimation(sourcePosition));
    }

    private void MergeTabIntoGroup(DeskGroup source, DeskGroup target)
    {
        if (GetWindowKey(source).Equals(GetWindowKey(target), StringComparison.OrdinalIgnoreCase)) return;
        var remainingSourceMembers = GetTabMembers(source)
            .Where(member => !ReferenceEquals(member, source)).ToArray();
        var targetMembers = GetTabMembers(target);
        var clusterId = target.TabGroupId ?? Guid.NewGuid().ToString("N");
        var combined = BuildTabMergeMembers(targetMembers, source);
        var sourcePosition = new System.Windows.Point(source.X, source.Y);
        ApplyGroupTopologyChange(() =>
        {
            NormalizeDetachedCluster(remainingSourceMembers);
            for (var index = 0; index < combined.Length; index++)
                ApplyClusterState(combined[index], target, clusterId, index);
        }, source, window => window.PlayMergeCompletedAnimation(sourcePosition));
    }

    private static void ApplyClusterState(DeskGroup member, DeskGroup target, string clusterId, int tabOrder)
    {
        member.TabGroupId = clusterId;
        member.TabOrder = tabOrder;
        member.X = target.X;
        member.Y = target.Y;
        member.Width = target.Width;
        member.Height = target.Height;
        member.IsCollapsed = target.IsCollapsed;
    }

    private static DeskGroup[] BuildTabMergeMembers(IEnumerable<DeskGroup> targetMembers, DeskGroup source) =>
        targetMembers.Where(member => !ReferenceEquals(member, source)).Append(source).Distinct().ToArray();

    internal static bool ValidateTabMergeAppendForSmokeTest()
    {
        var first = new DeskGroup { Name = "first" };
        var second = new DeskGroup { Name = "second" };
        var source = new DeskGroup { Name = "source" };
        return BuildTabMergeMembers([first, second], source).SequenceEqual([first, second, source]);
    }

    private void DetachGroup(DeskGroup group, System.Windows.Point pointerPosition)
    {
        var remainingMembers = GetTabMembers(group).Where(member => !ReferenceEquals(member, group)).ToArray();
        if (remainingMembers.Length == 0) return;
        var sourcePosition = new System.Windows.Point(group.X, group.Y);
        var sourceWindow = FindWindowForGroup(group.Id);
        // 回调传入物理屏幕坐标，先通过源窗口换算为 WPF DIP，与预览使用同一坐标空间。
        var localPointer = sourceWindow?.PointFromScreen(pointerPosition);
        var pointer = localPointer is { } local && sourceWindow is not null
            ? new System.Windows.Point(sourceWindow.Left + local.X, sourceWindow.Top + local.Y)
            : pointerPosition;
        var bounds = GroupDetachPlacement.GetBounds(pointer, new System.Windows.Size(group.Width, group.Height),
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        ApplyGroupTopologyChange(() =>
        {
            group.TabGroupId = null;
            group.TabOrder = 0;
            group.X = bounds.X;
            group.Y = bounds.Y;
            group.IsCollapsed = false;
            NormalizeDetachedCluster(remainingMembers);
        }, group, window => window.PlayDetachedAnimation(sourcePosition));
    }

    private void ApplyGroupTopologyChange(
        Action change,
        DeskGroup activeGroup,
        Action<DeskGroupWindow> completedAnimation)
    {
        var snapshots = Groups.ToDictionary(group => group, GroupTopologySnapshot.Capture);
        var expectedGroupIds = Groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignmentCounts = CountAssignmentsByGroup(_layout.NativeDesktop.Assignments);
        var arrivedThroughPreview = FindWindowForGroup(activeGroup.Id)?.HasGroupDragPreview == true;
        try
        {
            change();
            ValidateGroupTopology(Groups, expectedGroupIds, assignmentCounts, _layout.NativeDesktop.Assignments);
            SynchronizeGroupWindows(activeGroup);
            if (!arrivedThroughPreview && FindWindowForGroup(activeGroup.Id) is { } activeWindow)
                completedAnimation(activeWindow);
            ScheduleSave();
        }
        catch (Exception exception)
        {
            foreach (var (group, snapshot) in snapshots) snapshot.Restore(group);
            SynchronizeGroupWindows(activeGroup);
            MessageBox.Show($"合并未完成，已恢复原收纳盒和文件归属。\n\n{exception.Message}",
                "MiniC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SynchronizeGroupWindows(DeskGroup activeGroup)
    {
        // 差量修复宿主，未参与本次合并/拆分的收纳盒保持窗口、内容和动画时钟不变。
        EnsureGroupWindowCoverage();
        if (FindWindowForGroup(activeGroup.Id) is { } activeWindow && !ReferenceEquals(activeWindow.Group, activeGroup))
            activeWindow.ConfigureTabs(GetTabMembers(activeGroup), activeGroup);
        _desktopSurface?.RefreshDesktopPlacement();
    }

    private static Dictionary<string, int> CountAssignmentsByGroup(
        IReadOnlyDictionary<string, string> assignments) =>
        assignments.Values.GroupBy(groupId => groupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static void ValidateGroupTopology(
        IReadOnlyCollection<DeskGroup> groups,
        IReadOnlySet<string> expectedGroupIds,
        IReadOnlyDictionary<string, int> expectedAssignmentCounts,
        IReadOnlyDictionary<string, string> assignments)
    {
        var groupIds = groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groupIds.Count != groups.Count || !groupIds.SetEquals(expectedGroupIds))
            throw new InvalidOperationException("合并过程中收纳盒数量或标识发生了变化。");
        var currentAssignmentCounts = CountAssignmentsByGroup(assignments);
        if (expectedAssignmentCounts.Count != currentAssignmentCounts.Count
            || expectedAssignmentCounts.Any(entry =>
                currentAssignmentCounts.GetValueOrDefault(entry.Key) != entry.Value)
            || currentAssignmentCounts.Keys.Any(groupId => !groupIds.Contains(groupId)))
            throw new InvalidOperationException("收纳盒文件归属在合并过程中发生了变化。");

        foreach (var cluster in groups.Where(group => group.TabGroupId is not null)
                     .GroupBy(group => group.TabGroupId!, StringComparer.OrdinalIgnoreCase))
        {
            var orders = cluster.Select(group => group.TabOrder).Order().ToArray();
            if (orders.Length < 2 || !orders.SequenceEqual(Enumerable.Range(0, orders.Length)))
                throw new InvalidOperationException("合并后的标签顺序不完整。");
        }
    }

    internal static bool ValidateGroupTopologyGuardForSmokeTest()
    {
        var first = new DeskGroup { Id = "first", Name = "first", TabGroupId = "cluster", TabOrder = 0 };
        var second = new DeskGroup { Id = "second", Name = "second", TabGroupId = "cluster", TabOrder = 1 };
        DeskGroup[] groups = [first, second];
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["item-a"] = first.Id, ["item-b"] = second.Id };
        var groupIds = groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = CountAssignmentsByGroup(assignments);
        ValidateGroupTopology(groups, groupIds, counts, assignments);
        try
        {
            ValidateGroupTopology([first], groupIds, counts, assignments);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private readonly record struct GroupTopologySnapshot(
        string? TabGroupId,
        int TabOrder,
        double X,
        double Y,
        double Width,
        double Height,
        bool IsCollapsed)
    {
        public static GroupTopologySnapshot Capture(DeskGroup group) => new(
            group.TabGroupId, group.TabOrder, group.X, group.Y,
            group.Width, group.Height, group.IsCollapsed);

        public void Restore(DeskGroup group)
        {
            group.TabGroupId = TabGroupId;
            group.TabOrder = TabOrder;
            group.X = X;
            group.Y = Y;
            group.Width = Width;
            group.Height = Height;
            group.IsCollapsed = IsCollapsed;
        }
    }

    private static void NormalizeDetachedCluster(IReadOnlyList<DeskGroup> members)
    {
        if (members.Count == 0) return;
        if (members.Count == 1)
        {
            members[0].TabGroupId = null;
            members[0].TabOrder = 0;
            return;
        }
        for (var index = 0; index < members.Count; index++) members[index].TabOrder = index;
    }

    private void ToggleBoxes()
    {
        _boxesVisible = !_boxesVisible;
        if (_boxesVisible)
        {
            foreach (var window in _windows.Values) window.Show();
            foreach (var window in _windows.Values) window.RefreshDesktopPlacement();
        }
        else
        {
            foreach (var window in _windows.Values) window.Hide();
        }
    }

    private void ToggleStartup()
    {
        var enabled = !(_layout.StartWithWindows ?? true);
        try
        {
            StartupService.SetEnabled(enabled);
            _layout.StartWithWindows = enabled;
            _startupMenuItem.Checked = enabled;
            ScheduleSave();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法修改开机启动设置。\n\n{ex.Message}", "MiniC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyTheme(string theme)
    {
        _layout.DefaultTheme = theme;
        foreach (var group in Groups) group.Theme = theme;
        ScheduleSave();
    }

    private void ApplyMaterial(string material)
    {
        _layout.DefaultMaterial = material;
        foreach (var group in Groups) group.Material = material;
        ScheduleSave();
    }

    private void ApplyOpacity(double opacity)
    {
        _layout.DefaultOpacity = opacity;
        foreach (var group in Groups) group.Opacity = opacity;
        ScheduleSave();
    }

    private (string Theme, string Material, double Opacity) GetUnifiedStyle()
    {
        var first = Groups.FirstOrDefault();
        return (_layout.DefaultTheme ?? first?.Theme ?? "White",
            _layout.DefaultMaterial ?? first?.Material ?? "Clear",
            _layout.DefaultOpacity ?? first?.Opacity ?? 0.62);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveLayoutAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            _layout.Groups = Groups.Select(group => new GroupState
            {
                Id = group.Id,
                Name = group.Name,
                X = group.X,
                Y = group.Y,
                Width = group.Width,
                Height = group.Height,
                Theme = group.Theme,
                Material = group.Material,
                Opacity = group.Opacity,
                ViewMode = group.ViewMode,
                ItemOrder = group.ItemOrder.ToList(),
                IsCollapsed = group.IsCollapsed,
                TabGroupId = group.TabGroupId,
                TabOrder = group.TabOrder
            }).ToList();
            await _layoutStore.SaveAsync(_layout);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void RestoreNativePositions(IEnumerable<string> paths)
    {
        var placements = paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => _layout.NativeDesktop.ManagedPositions.ContainsKey(path))
            .Select(path =>
            {
                var position = _layout.NativeDesktop.ManagedPositions[path];
                return new NativeDesktopPlacement(path, position.X, position.Y);
            }).ToArray();
        _ = _nativeDesktopService.Arrange(placements);
    }

    private async Task ExitAsync()
    {
        if (_isExiting) return;
        _isExiting = true;
        _recycleBinIcons.Stop();
        _saveTimer.Stop();
        _reconcileTimer.Stop();
        await _renameGate.WaitAsync();
        _renameGate.Release();
        RestoreNativePositions(_layout.NativeDesktop.Assignments.Keys);
        RestoreNativePositions(DesktopItems.Where(item => !item.IsVirtual).Select(item => item.Path));
        _ = _nativeDesktopService.SetDesktopIconLayerVisible(true);
        try { await SaveLayoutAsync(); }
        catch (Exception exception) { MiniCLogger.Error(nameof(DesktopCoordinator), exception, "Final layout save failed."); }
        _desktopSurface?.ClosePermanently();
        _desktopSurface = null;
        foreach (var window in _windows.Values) window.ClosePermanently();
        _windows.Clear();
        _shutdownApplication();
    }

    private IntPtr MessageWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == ShellChangeMessage)
        {
            _desktopFileService.HandleShellChangeNotification(wParam, lParam);
            handled = true;
            return IntPtr.Zero;
        }
        if (message == ClipboardUpdateMessage)
        {
            Application.Current.Dispatcher.InvokeAsync(RefreshClipboardItemState);
            return IntPtr.Zero;
        }
        if (message == HotkeyMessage && wParam.ToInt32() == HotkeyId)
        {
            ToggleBoxes();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isExiting = true;
        _recycleBinIcons.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemDisplaySettingsChanged;
        _saveTimer.Stop();
        _reconcileTimer.Stop();
        RestoreNativePositions(_layout.NativeDesktop.Assignments.Keys);
        RestoreNativePositions(DesktopItems.Where(item => !item.IsVirtual).Select(item => item.Path));
        _ = _nativeDesktopService.SetDesktopIconLayerVisible(true);
        _desktopFileService.DesktopChanged -= DesktopFileService_DesktopChanged;
        _desktopFileService.Dispose();
        foreach (var window in _windows.Values) window.ClosePermanently();
        _windows.Clear();
        _desktopSurface?.ClosePermanently();
        _desktopSurface = null;
        _trayPanel?.Close();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
        if (_clipboardListenerRegistered)
            _ = RemoveClipboardFormatListener(_messageWindow.Handle);
        _ = UnregisterHotKey(_messageWindow.Handle, HotkeyId);
        _messageWindow.RemoveHook(MessageWindowProc);
        _messageWindow.Dispose();
        _saveGate.Dispose();
        _refreshGate.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr windowHandle);
}
