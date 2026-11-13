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
using MiniC.Widgets;
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
    private const int SettingsChangedMessage = 0x001A;
    private const int DisplayChangedMessage = 0x007E;
    private const int DpiChangedMessage = 0x02E0;
    private const int ThemeChangedMessage = 0x031E;

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
    private readonly DispatcherTimer _wallpaperRefreshTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _renameGate = new(1, 1);
    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _applicationIcon;
    private readonly WinForms.ToolStripMenuItem _startupMenuItem;
    private readonly HwndSource _messageWindow;
    private readonly WidgetStore _widgetStore = new();
    private readonly WidgetWeatherService _widgetWeather = new();
    private string? _lastReminderWidgetId;
    private readonly Dictionary<string, WidgetState> _widgets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BoxHostState> _boxHosts = [];
    private readonly DispatcherTimer _widgetReminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
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
        _wallpaperRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _wallpaperRefreshTimer.Tick += async (_, _) =>
        {
            _wallpaperRefreshTimer.Stop();
            if (_isExiting || _isDisposed) return;
            try
            {
                WallpaperTextureService.Invalidate();
                await Task.WhenAll(_windows.Values.ToArray().Select(window => window.RefreshWallpaperAsync()));
            }
            catch (Exception error)
            {
                MiniCLogger.Error(nameof(DesktopCoordinator), error, "Wallpaper refresh failed.");
            }
        };
        _widgetReminderTimer.Tick += (_, _) => CheckWidgetReminders();

        _applicationIcon = Environment.ProcessPath is { } processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
              ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone()
            : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _trayIcon = new WinForms.NotifyIcon { Icon = _applicationIcon, Text = "MiniC", Visible = false };
        _trayIcon.BalloonTipClicked += (_, _) => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_lastReminderWidgetId is { } id)
                ShowBoxTab(new BoxTabRef(BoxContentKind.Widget, id));
        });
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                Application.Current.Dispatcher.InvokeAsync(ActivateBoxes);
        };
        var trayMenu = new WinForms.ContextMenuStrip();
        var widgetsMenu = new WinForms.ToolStripMenuItem("新建组件");
        foreach (var option in BoxCreationCatalog.Options)
        {
            if (option.WidgetKind is null)
                trayMenu.Items.Add(option.MenuLabel, null, (_, _) =>
                    Application.Current.Dispatcher.InvokeAsync(() => CreateBox(option)));
            else
                widgetsMenu.DropDownItems.Add(option.Name, null, (_, _) =>
                    Application.Current.Dispatcher.InvokeAsync(() => CreateBox(option)));
        }
        trayMenu.Items.Add(widgetsMenu);
        trayMenu.Items.Add("显示所有组件", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ShowAllWidgets));
        trayMenu.Items.Add("显示 / 隐藏收纳盒", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ToggleBoxes));
        trayMenu.Items.Add("刷新", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            ScheduleWallpaperRefresh();
            await RefreshAsync();
        }));
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
            ScheduleWallpaperRefresh();
            _desktopSurface?.UpdateDisplayBounds();
            await RefreshAsync();
        });
    }

    private void ScheduleWallpaperRefresh()
    {
        if (_isExiting || _isDisposed) return;
        _wallpaperRefreshTimer.Stop();
        _wallpaperRefreshTimer.Start();
    }

    public async Task InitializeAsync()
    {
        _ = _nativeDesktopService.SetDesktopIconLayerVisible(true);
        _layout = await _layoutStore.LoadAsync();
        _layout.StartWithWindows = StartupService.IsEnabled();
        _startupMenuItem.Checked = _layout.StartWithWindows.Value;
        LayoutRepairService.EnsureReferencedGroups(_layout,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth);
        if (_layout.BoxHosts is null) SeparateOverlappingGroups(_layout.Groups);

        foreach (var widget in _widgetStore.Widgets()) _widgets[widget.Id] = widget;
        BoxAppearanceDefaults.Initialize(_layout, _widgets.Values, _widgetStore.Save);
        ShowDesktopSurface();
        _desktopSurface?.SetViewMode(_layout.NativeDesktop.ViewMode);

        foreach (var state in _layout.Groups)
        {
            var group = CreateGroupModel(state);
            Groups.Add(group);
        }
        _boxHosts.AddRange(BoxTopologyService.Reconcile(_layout.BoxHosts, _layout.Groups,
            _widgets.Values.Select(widget => (widget.Id, widget.X, widget.Y, widget.Width, widget.Height, widget.Collapsed)).ToArray()));
        foreach (var host in _boxHosts)
        {
            var group = host.Tabs.Where(tab => tab.Kind == BoxContentKind.DesktopGroup)
                .Select(tab => Groups.FirstOrDefault(candidate => candidate.Id == tab.Id)).FirstOrDefault(candidate => candidate is not null);
            var widget = host.Tabs.Where(tab => tab.Kind == BoxContentKind.Widget)
                .Select(tab => _widgets.GetValueOrDefault(tab.Id)).FirstOrDefault(candidate => candidate is not null);
            host.Appearance ??= group?.Appearance ?? widget?.Appearance ?? BoxAppearanceDefaults.CreateAppearance();
            host.Theme ??= group?.Theme ?? widget?.Theme ?? BoxAppearanceDefaults.Theme;
            host.Material ??= group?.Material ?? widget?.Material ?? BoxAppearanceDefaults.Material;
            host.Opacity ??= group?.Opacity ?? widget?.Opacity ?? BoxAppearanceDefaults.Opacity;
        }
        SyncLegacyGroupTopology();
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
        _widgetReminderTimer.Start();
        CheckWidgetReminders();
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
            (item, newName) => RenameItemCoreAsync(item, newName, _desktopSurface),
            ShowDesktopItemMenuAsync,
            () => ClearTransientState(_desktopSurface),
            ShowDesktopBackgroundMenuAsync);
        _desktopSurface = new DesktopSurfaceWindow(
            DesktopItems, callbacks, _desktopLayerHostService, _desktopDragStateService);
        _desktopSurface.Show();
    }

    public void ActivateBoxes()
    {
        if (_isExiting || _isDisposed) return;
        if (!_boxesVisible)
        {
            _boxesVisible = true;
            foreach (var window in _windows.Values) window.Show();
        }

        foreach (var window in _windows.Values.Where(window => window.IsVisible))
            window.RefreshDesktopPlacement();
        _windows.Values.FirstOrDefault(window => window.IsVisible)?.Activate();
    }

    private static string WidgetTitle(WidgetKind kind) => kind switch
    {
        WidgetKind.Note => "备忘录", WidgetKind.Tasks => "待办", WidgetKind.Schedule => "日程",
        WidgetKind.Calendar => "日历", WidgetKind.Planner => "日程中心", _ => "天气"
    };

    private void CreateBox(BoxCreationOption option)
    {
        if (option.WidgetKind is { } kind) AddWidget(kind);
        else AddGroup();
    }

    private void AddWidget(WidgetKind kind)
    {
        var style = GetDefaultStyle();
        var size = BoxSizePolicy.GetNewBoxSize(_boxHosts);
        var location = FindNewBoxLocation(new System.Windows.Point(120, 120), size);
        var state = new WidgetState { Kind = kind, Title = WidgetTitle(kind),
            X = location.X, Y = location.Y,
            Material = style.Material, Theme = style.Theme, Opacity = style.Opacity, Appearance = BoxAppearanceDefaults.CreateAppearance(),
            Width = size.Width,
            Height = size.Height,
            ViewMode = kind == WidgetKind.Weather ? "Small" : "" };
        _widgetStore.Save(state);
        _widgets[state.Id] = state;
        _boxHosts.Add(new BoxHostState { Tabs = [new BoxTabRef(BoxContentKind.Widget, state.Id)],
            ActiveTab = new BoxTabRef(BoxContentKind.Widget, state.Id),
            X = state.X, Y = state.Y, Width = state.Width, Height = state.Height,
            Appearance = state.Appearance, Theme = state.Theme, Material = state.Material, Opacity = state.Opacity });
        EnsureGroupWindowCoverage();
        ShowBoxTab(new BoxTabRef(BoxContentKind.Widget, state.Id));
        ScheduleSave();
    }

    private void ShowAllWidgets()
    {
        foreach (var tab in _widgets.Keys.Select(id => new BoxTabRef(BoxContentKind.Widget, id)))
            ShowBoxTab(tab);
    }
    private void CheckWidgetReminders()
    {
        try
        {
            var due = _widgetStore.ClaimDue(DateTimeOffset.UtcNow);
            if (due.Count == 0) return;
            _lastReminderWidgetId = due[0].WidgetId;
            var message = string.Join("、", due.Take(3).Select(item => item.Title));
            if (due.Count > 3) message += $" 等 {due.Count} 条";
            _trayIcon.ShowBalloonTip(8000, "MiniC 提醒", message, WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex) { MiniCLogger.Error(nameof(DesktopCoordinator), ex, "Widget reminder check failed."); }
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
            Opacity = BackgroundTransparency.NormalizeOpacity(state.Opacity),
            Appearance = BoxAppearanceDefaults.CreateAppearance(),
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

    private void ShowGroupWindow(DeskGroup group, BoxHostState host, double? arrivalScale = null)
    {
        var windowKey = host.Id;
        if (_windows.ContainsKey(windowKey)) return;
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
            () => _desktopSurface?.RefreshDesktopPlacement())
        {
            ShowBackgroundMenuAsync = ShowGroupBackgroundMenuAsync,
            SnapBounds = bounds => SnapBoxBounds(windowKey, bounds)
        };
        var window = new DeskGroupWindow(
            group, callbacks, _desktopLayerHostService, _desktopDragStateService);
        ConfigureHostWindow(window, host);
        _windows[windowKey] = window;
        if (arrivalScale is not null && host.ActiveTab is { } tab) window.SwitchTab(tab);
        if (arrivalScale is { } scale) window.PrepareBoxTransferArrival(scale);
        window.Show();
        if (arrivalScale is not null) window.PlayBoxTransferArrival();
        _desktopSurface?.RefreshDesktopPlacement();
    }

    private DeskGroupWindow? FindWindowForGroup(string groupId) =>
        _windows.Values.FirstOrDefault(window => window.ContainsGroup(groupId));

    private Rect SnapBoxBounds(string sourceId, Rect proposed)
    {
        if (!_windows.TryGetValue(sourceId, out var source)) return proposed;
        var neighbors = new List<Rect>();
        foreach (var (id, window) in _windows)
        {
            if (id == sourceId || !window.IsVisible) continue;
            if (NativeWindowPlacement.TryGetBounds(new WindowInteropHelper(window).Handle, out var bounds))
                neighbors.Add(bounds);
        }
        return BoxSnapGeometry.Snap(proposed, neighbors, System.Windows.Media.VisualTreeHelper.GetDpi(source).DpiScaleX);
    }

    private void ConfigureHostWindow(DeskGroupWindow window, BoxHostState host) =>
        window.ConfigureHost(host, Groups.ToDictionary(group => group.Id, StringComparer.OrdinalIgnoreCase),
            _widgets, _widgetStore, _widgetWeather, AddWidget, DeleteWidget, ScheduleSave,
            MergeBoxContent, MergeBoxHost, DetachBoxContent, ReorderBoxContent, FindBoxMergeTarget,
            SetBoxMergeTarget, FindWholeBoxMergeTarget);

    private void ShowBoxTab(BoxTabRef tab)
    {
        var host = _boxHosts.FirstOrDefault(candidate => candidate.Tabs.Contains(tab));
        if (host is null || !_windows.TryGetValue(host.Id, out var window)) return;
        if (!window.IsVisible) window.Show();
        window.SwitchTab(tab);
        window.Activate();
    }

    private void DeleteWidget(WidgetState widget)
    {
        _widgetStore.DeleteWidget(widget.Id);
        _widgets.Remove(widget.Id);
        BoxTopologyService.Remove(_boxHosts, new BoxTabRef(BoxContentKind.Widget, widget.Id));
        SyncLegacyGroupTopology();
        EnsureGroupWindowCoverage();
        ScheduleSave();
    }

    private void MergeBoxContent(BoxTabRef source, BoxTabRef target)
    {
        if (source == target) return;
        var origin = _boxHosts.Single(host => host.Tabs.Contains(source));
        if (ReferenceEquals(origin, _boxHosts.Single(host => host.Tabs.Contains(target)))) return;
        ChangeBoxTopology(() => BoxTopologyService.Merge(_boxHosts, source, target), source);
    }

    private void MergeBoxHost(BoxTabRef source, BoxTabRef target)
    {
        if (source == target) return;
        var origin = _boxHosts.Single(host => host.Tabs.Contains(source));
        if (ReferenceEquals(origin, _boxHosts.Single(host => host.Tabs.Contains(target)))) return;
        ChangeBoxTopology(() => BoxTopologyService.MergeHost(_boxHosts, source, target), source);
    }

    private void DetachBoxContent(BoxTabRef tab, Rect bounds, double startScale)
    {
        var host = _boxHosts.Single(candidate => candidate.Tabs.Contains(tab));
        ChangeBoxTopology(() => BoxTopologyService.Detach(_boxHosts, tab, bounds.X, bounds.Y,
            bounds.Width, bounds.Height, false), tab, startScale);
    }

    private void ChangeBoxTopology(Action change, BoxTabRef activeTab, double? arrivalScale = null)
    {
        var previousHosts = _boxHosts.Select(CloneHost).ToArray();
        var expected = Groups.Select(group => new BoxTabRef(BoxContentKind.DesktopGroup, group.Id))
            .Concat(_widgets.Keys.Select(id => new BoxTabRef(BoxContentKind.Widget, id))).ToArray();
        try
        {
            change();
            BoxTopologyService.Validate(_boxHosts, expected);
            SyncLegacyGroupTopology();
            EnsureGroupWindowCoverage(activeTab, arrivalScale);
            ShowBoxTab(activeTab);
            ScheduleSave();
        }
        catch (Exception error)
        {
            _boxHosts.Clear();
            _boxHosts.AddRange(previousHosts);
            var recovered = false;
            try
            {
                SyncLegacyGroupTopology();
                EnsureGroupWindowCoverage();
                recovered = true;
            }
            catch (Exception recoveryError)
            {
                MiniCLogger.Warning(nameof(DesktopCoordinator), $"Box recovery failed: {recoveryError}");
            }
            MiniCLogger.Warning(nameof(DesktopCoordinator), $"Box topology change failed: {error}");
            MessageBox.Show($"盒子操作未完成，{(recovered ? "已恢复原布局" : "恢复失败，请重启 MiniC 并检查日志")}。\n\n{error.Message}",
                "MiniC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReorderBoxContent(BoxTabRef tab, IReadOnlyList<BoxTabRef> tabs)
    {
        BoxTopologyService.Reorder(_boxHosts.Single(host => host.Tabs.Contains(tab)), tabs);
        SyncLegacyGroupTopology();
        ScheduleSave();
    }

    private void SyncLegacyGroupTopology()
    {
        foreach (var host in _boxHosts)
        {
            var groups = host.Tabs.Where(tab => tab.Kind == BoxContentKind.DesktopGroup)
                .Select(tab => Groups.First(group => group.Id == tab.Id)).ToArray();
            for (var index = 0; index < groups.Length; index++)
            {
                groups[index].TabGroupId = groups.Length > 1 ? host.Id : null;
                groups[index].TabOrder = index;
                groups[index].X = host.X; groups[index].Y = host.Y;
                groups[index].Width = host.Width; groups[index].Height = host.Height;
                groups[index].IsCollapsed = host.IsCollapsed;
            }
        }
    }


    private void EnsureGroupWindowCoverage(BoxTabRef? arrivingTab = null, double? arrivalScale = null)
    {
        BoxTopologyService.Validate(_boxHosts,
            Groups.Select(group => new BoxTabRef(BoxContentKind.DesktopGroup, group.Id))
                .Concat(_widgets.Keys.Select(id => new BoxTabRef(BoxContentKind.Widget, id))).ToArray());
        var representatives = _boxHosts.ToDictionary(host => host.Id, host => host,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (windowKey, host) in representatives)
        {
            if (!_windows.TryGetValue(windowKey, out var window))
            {
                var representative = host.Tabs.Where(tab => tab.Kind == BoxContentKind.DesktopGroup)
                    .Select(tab => Groups.First(group => group.Id == tab.Id)).FirstOrDefault();
                var fallback = representative ?? CreateHostAnchor(host);
                ShowGroupWindow(fallback, host, arrivingTab is { } tab && host.Tabs.Contains(tab)
                    ? arrivalScale : null);
                continue;
            }
            ConfigureHostWindow(window, host);
            if (_boxesVisible && !window.IsVisible) window.Show();
            if (_boxesVisible && !window.IsActive) window.RefreshDesktopPlacement();
        }

        // Only retire the source window after every destination is configured.
        foreach (var staleKey in _windows.Keys.Where(key => !representatives.ContainsKey(key)).ToArray())
        {
            var staleWindow = _windows[staleKey];
            _windows.Remove(staleKey);
            staleWindow.ClosePermanently();
        }
    }

    private static DeskGroup CreateHostAnchor(BoxHostState host) => new()
    {
        Id = "host-anchor-" + host.Id, Name = "组件", X = host.X, Y = host.Y,
        Width = host.Width, Height = host.Height,
        Theme = host.Theme ?? "White", Material = host.Material ?? "Crystal",
        Opacity = host.Opacity ?? BoxAppearanceDefaults.Opacity,
        Appearance = host.Appearance ?? BoxAppearanceDefaults.CreateAppearance(),
        IsCollapsed = host.IsCollapsed
    };

    private void AddGroup() => AddGroupNear(Groups.LastOrDefault());

    private void AddGroupNear(DeskGroup? source)
    {
        var style = GetDefaultStyle();
        var size = BoxSizePolicy.GetNewBoxSize(_boxHosts);
        var index = Groups.Count + 1;
        var location = FindNewBoxLocation(source is null ? new System.Windows.Point(70, 130)
            : new System.Windows.Point(source.X + source.Width + BoxCreationPlacement.Gap, source.Y), size, source);
        var group = new DeskGroup
        {
            Name = $"收纳盒 {index}",
            X = location.X,
            Y = location.Y,
            Width = size.Width,
            Height = size.Height,
            Theme = style.Theme,
            Material = style.Material,
            Appearance = BoxAppearanceDefaults.CreateAppearance(),
            Opacity = style.Opacity
        };
        group.PropertyChanged += (_, _) => ScheduleSave();
        Groups.Add(group);
        _boxHosts.Add(new BoxHostState { Id = group.Id, Tabs = [new BoxTabRef(BoxContentKind.DesktopGroup, group.Id)],
            ActiveTab = new BoxTabRef(BoxContentKind.DesktopGroup, group.Id),
            X = group.X, Y = group.Y, Width = group.Width, Height = group.Height,
            Theme = group.Theme, Material = group.Material, Opacity = group.Opacity, Appearance = group.Appearance });
        EnsureGroupWindowCoverage();
        ScheduleSave();
    }

    private System.Windows.Point FindNewBoxLocation(System.Windows.Point preferred, BoxSize size, DeskGroup? source = null)
    {
        var anchor = source is null ? _windows.Values.FirstOrDefault(window => window.IsVisible)
            : FindWindowForGroup(source.Id);
        var workArea = SystemParameters.WorkArea;
        if (anchor is { IsVisible: true })
        {
            var physical = WinForms.Screen.FromHandle(new WindowInteropHelper(anchor).Handle).WorkingArea;
            var topLeft = anchor.PointFromScreen(new System.Windows.Point(physical.Left, physical.Top));
            var bottomRight = anchor.PointFromScreen(new System.Windows.Point(physical.Right, physical.Bottom));
            workArea = new Rect(new System.Windows.Point(anchor.Left + topLeft.X, anchor.Top + topLeft.Y),
                new System.Windows.Point(anchor.Left + bottomRight.X, anchor.Top + bottomRight.Y));
        }
        var occupied = _boxHosts.Select(host => _windows.TryGetValue(host.Id, out var window)
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : new Rect(host.X, host.Y, host.Width, host.Height));
        return BoxCreationPlacement.Find(preferred, new System.Windows.Size(size.Width, size.Height), workArea, occupied);
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
            placedPaths, screenPoint, DesktopItems, _layout.NativeDesktop.ManagedPositions,
            _layout.NativeDesktop.AlignToGrid);
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
            existingPaths, screenPoint, DesktopItems, _layout.NativeDesktop.ManagedPositions,
            _layout.NativeDesktop.AlignToGrid);
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
            existingPaths, screenPoint, DesktopItems, _layout.NativeDesktop.ManagedPositions,
            _layout.NativeDesktop.AlignToGrid);
        await RefreshAsync();
        ScheduleSave();
    }

    private async Task<bool> RenameItemAsync(DeskGroup group, DesktopItem item, string newName)
        => await RenameItemCoreAsync(item, newName, FindWindowForGroup(group.Id));

    private async Task<bool> RenameItemCoreAsync(DesktopItem item, string newName, Window? owner)
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
                if (owner is { IsVisible: true })
                    MessageBox.Show(owner, result.Error ?? "无法重命名该项目。", "重命名失败",
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
            item.NeedsIconRefresh = true;
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
        ApplyDesktopOrdering();
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
        if (_desktopSurface is null) return;
        await ShowBackgroundMenuCoreAsync(_desktopSurface, null, screenPoint);
    }

    private async Task ShowGroupBackgroundMenuAsync(DeskGroup group, System.Drawing.Point screenPoint)
    {
        var window = FindWindowForGroup(group.Id);
        if (window is null) return;
        await ShowBackgroundMenuCoreAsync(window, group, screenPoint);
    }

    private async Task ShowBackgroundMenuCoreAsync(
        Window sourceWindow,
        DeskGroup? targetGroup,
        System.Drawing.Point screenPoint)
    {
        if (_desktopBackgroundMenuActive) return;
        _desktopBackgroundMenuActive = true;
        await _refreshGate.WaitAsync();
        _refreshGate.Release();
        var pathsBeforeCommand = _nativeDesktopService.EnumerateDesktopItems()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = ShellContextMenuResult.Failed;
        IReadOnlyList<string> newItemPaths = [];
        var ownerWindow = new ShellMenuOwnerWindow(screenPoint);
        SetTransientPopupState(sourceWindow, true);
        try
        {
            var owner = ownerWindow.ShowAndGetHandle();
            result = _shellContextMenu.ShowDesktopBackground(
                owner, screenPoint, targetGroup is null ? _layout.NativeDesktop : null,
                targetGroup is null);
            if (result is ShellContextMenuResult.NewItemCreated or ShellContextMenuResult.PasteInvoked)
                newItemPaths = await WaitForDesktopCommandToSettleAsync(pathsBeforeCommand);
        }
        finally
        {
            ownerWindow.Close();
            SetTransientPopupState(sourceWindow, false);
            _desktopBackgroundMenuActive = false;
        }

        var deferredRenames = _deferredDesktopRenames.ToArray();
        _deferredDesktopRenames.Clear();
        ApplyRenames(deferredRenames);
        if (targetGroup is not null)
        {
            if (result is ShellContextMenuResult.NewItemCreated or ShellContextMenuResult.PasteInvoked)
            {
                foreach (var path in newItemPaths.Where(path =>
                             File.Exists(path) || Directory.Exists(path)))
                {
                    _layout.NativeDesktop.Assignments[path] = targetGroup.Id;
                    _layout.NativeDesktop.ManagedPositions.Remove(path);
                }
                await RefreshAsync();
                ScheduleSave();
                if (result == ShellContextMenuResult.NewItemCreated)
                    BeginRenameCreatedItemInGroup(targetGroup, newItemPaths);
            }
            else if (result is ShellContextMenuResult.CommandInvoked or ShellContextMenuResult.RefreshRequested)
            {
                await RefreshAsync();
                ScheduleSave();
            }
            else if (deferredRenames.Length > 0)
            {
                await RefreshAsync();
                ScheduleSave();
            }
            ClearTransientState();
            return;
        }

        switch (result)
        {
            case var requested when BoxCreationCatalog.Find(requested) is { } option:
                CreateBox(option);
                break;
            case ShellContextMenuResult.RefreshRequested:
                await RefreshAsync();
                ScheduleSave();
                break;
            case ShellContextMenuResult.ToggleGroupsRequested:
                ToggleBoxes();
                break;
            case ShellContextMenuResult.CommandInvoked:
            case ShellContextMenuResult.PasteInvoked:
                await RefreshAsync();
                ScheduleSave();
                break;
            case ShellContextMenuResult.NewItemCreated:
                await RefreshAsync();
                PlaceCreatedItemsAtPointer(newItemPaths, screenPoint);
                ScheduleSave();
                break;
            case ShellContextMenuResult.DesktopViewSmall:
            case ShellContextMenuResult.DesktopViewMedium:
            case ShellContextMenuResult.DesktopViewLarge:
            case ShellContextMenuResult.DesktopSortName:
            case ShellContextMenuResult.DesktopSortSize:
            case ShellContextMenuResult.DesktopSortType:
            case ShellContextMenuResult.DesktopSortDate:
            case ShellContextMenuResult.DesktopSortAscending:
            case ShellContextMenuResult.DesktopSortDescending:
            case ShellContextMenuResult.DesktopGroupNone:
            case ShellContextMenuResult.DesktopGroupName:
            case ShellContextMenuResult.DesktopGroupSize:
            case ShellContextMenuResult.DesktopGroupType:
            case ShellContextMenuResult.DesktopGroupDate:
            case ShellContextMenuResult.DesktopGroupAscending:
            case ShellContextMenuResult.DesktopGroupDescending:
            case ShellContextMenuResult.DesktopAutoArrange:
            case ShellContextMenuResult.DesktopAlignGrid:
                ApplyDesktopLayoutCommand(result);
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

    private void BeginRenameCreatedItemInGroup(DeskGroup group, IReadOnlyList<string> paths)
    {
        var path = paths.Count == 1 ? paths[0] : paths.LastOrDefault();
        if (string.IsNullOrWhiteSpace(path)) return;
        var item = group.Items.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is not null) FindWindowForGroup(group.Id)?.BeginRename(item);
    }

    private void ApplyDesktopLayoutCommand(ShellContextMenuResult result)
    {
        var state = _layout.NativeDesktop;
        switch (result)
        {
            case ShellContextMenuResult.DesktopViewSmall:
                state.ViewMode = "Small";
                _desktopSurface?.SetViewMode(state.ViewMode);
                break;
            case ShellContextMenuResult.DesktopViewMedium:
                state.ViewMode = "Medium";
                _desktopSurface?.SetViewMode(state.ViewMode);
                break;
            case ShellContextMenuResult.DesktopViewLarge:
                state.ViewMode = "Large";
                _desktopSurface?.SetViewMode(state.ViewMode);
                break;
            case ShellContextMenuResult.DesktopSortName: state.SortMode = "Name"; break;
            case ShellContextMenuResult.DesktopSortSize: state.SortMode = "Size"; break;
            case ShellContextMenuResult.DesktopSortType: state.SortMode = "Type"; break;
            case ShellContextMenuResult.DesktopSortDate: state.SortMode = "Date"; break;
            case ShellContextMenuResult.DesktopSortAscending: state.SortAscending = true; break;
            case ShellContextMenuResult.DesktopSortDescending: state.SortAscending = false; break;
            case ShellContextMenuResult.DesktopGroupNone: state.GroupMode = "None"; break;
            case ShellContextMenuResult.DesktopGroupName: state.GroupMode = "Name"; break;
            case ShellContextMenuResult.DesktopGroupSize: state.GroupMode = "Size"; break;
            case ShellContextMenuResult.DesktopGroupType: state.GroupMode = "Type"; break;
            case ShellContextMenuResult.DesktopGroupDate: state.GroupMode = "Date"; break;
            case ShellContextMenuResult.DesktopGroupAscending: state.GroupAscending = true; break;
            case ShellContextMenuResult.DesktopGroupDescending: state.GroupAscending = false; break;
            case ShellContextMenuResult.DesktopAutoArrange: state.AutoArrange = !state.AutoArrange; break;
            case ShellContextMenuResult.DesktopAlignGrid:
                state.AlignToGrid = !state.AlignToGrid;
                if (state.AlignToGrid)
                    _desktopIconPlacementService.SnapToGrid(DesktopItems, state.ManagedPositions);
                break;
            default: return;
        }

        if (result is not (ShellContextMenuResult.DesktopViewSmall
            or ShellContextMenuResult.DesktopViewMedium
            or ShellContextMenuResult.DesktopViewLarge))
            ApplyDesktopOrdering();
        foreach (var item in DesktopItems)
            if (state.ManagedPositions.TryGetValue(item.Path, out var position))
                _desktopSurface?.SetScreenPosition(item, position.X, position.Y);
        _desktopSurface?.SetViewMode(state.ViewMode);
        ScheduleSave();
    }

    private void ApplyDesktopOrdering()
    {
        var state = _layout.NativeDesktop;
        var ordered = OrderDesktopItems(DesktopItems, state).ToArray();
        SynchronizeItems(DesktopItems, ordered);
        if (state.AutoArrange)
            _desktopIconPlacementService.ArrangeInOrder(ordered, state.ManagedPositions);
    }

    /// <summary>按桌面状态生成稳定的视觉顺序，避免把不同类型的排序键强制比较为 object。</summary>
    internal static IReadOnlyList<DesktopItem> OrderDesktopItems(
        IReadOnlyList<DesktopItem> items,
        NativeDesktopState state)
    {
        IOrderedEnumerable<DesktopItem> ordered = state.GroupMode switch
        {
            "Size" when state.GroupAscending => items.OrderBy(GetDesktopItemSize),
            "Size" => items.OrderByDescending(GetDesktopItemSize),
            "Type" when state.GroupAscending => items.OrderBy(item => Path.GetExtension(item.Path), StringComparer.CurrentCultureIgnoreCase),
            "Type" => items.OrderByDescending(item => Path.GetExtension(item.Path), StringComparer.CurrentCultureIgnoreCase),
            "Date" when state.GroupAscending => items.OrderBy(item => item.LastWriteTicks),
            "Date" => items.OrderByDescending(item => item.LastWriteTicks),
            "Name" when state.GroupAscending => items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "Name" => items.OrderByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(_ => 0)
        };

        ordered = state.SortMode switch
        {
            "Size" when state.SortAscending => ordered.ThenBy(GetDesktopItemSize),
            "Size" => ordered.ThenByDescending(GetDesktopItemSize),
            "Type" when state.SortAscending => ordered.ThenBy(item => Path.GetExtension(item.Path), StringComparer.CurrentCultureIgnoreCase),
            "Type" => ordered.ThenByDescending(item => Path.GetExtension(item.Path), StringComparer.CurrentCultureIgnoreCase),
            "Date" when state.SortAscending => ordered.ThenBy(item => item.LastWriteTicks),
            "Date" => ordered.ThenByDescending(item => item.LastWriteTicks),
            _ when state.SortAscending => ordered.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => ordered.ThenByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        return ordered
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static long GetDesktopItemSize(DesktopItem item)
    {
        try { return File.Exists(item.Path) ? new FileInfo(item.Path).Length : 0; }
        catch { return 0; }
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
            _layout.NativeDesktop.ManagedPositions,
            _layout.NativeDesktop.AlignToGrid);
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
                    Group: entry.Value.ActiveGroup);
            })
            .LastOrDefault(entry => entry.Bounds.Contains(screenPoint)).Group;

    private BoxTabRef? FindBoxMergeTarget(BoxTabRef source, System.Windows.Point screenPoint)
        => FindBoxMergeTarget(source, screenPoint, wholeBox: false);

    private BoxTabRef? FindWholeBoxMergeTarget(BoxTabRef source, System.Windows.Point screenPoint)
        => FindBoxMergeTarget(source, screenPoint, wholeBox: true);

    private BoxTabRef? FindBoxMergeTarget(BoxTabRef source, System.Windows.Point screenPoint, bool wholeBox)
    {
        var sourceHost = _boxHosts.FirstOrDefault(host => host.Tabs.Contains(source));
        if (sourceHost is null) return null;
        var candidates = _windows.Values.Where(window => window.IsVisible && window.ActiveTab is not null)
            .ToDictionary(window => new WindowInteropHelper(window).Handle,
                window => new BoxDropTarget(window.HostId, window.ActiveTab!, window.GetMergeDropScreenBounds()));
        return BoxDropGeometry.FindTarget(sourceHost.Id,
            NativeWindowPlacement.TopToBottom(candidates.Keys).Select(handle => candidates[handle]), screenPoint, wholeBox);
    }

    private void SetBoxMergeTarget(BoxTabRef source, BoxTabRef? target)
    {
        var targetWindow = target is null ? null : _windows.Values.FirstOrDefault(window => window.ContainsTab(target));
        foreach (var window in _windows.Values)
            window.SetMergeDropTarget(ReferenceEquals(window, targetWindow));
    }

    private bool IsDesktopAt(System.Windows.Point screenPoint) =>
        _desktopSurface?.IsWindowAt((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)) == true
        || _nativeDesktopService.IsDesktopSurfaceAt(
            (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));

    private void DissolveGroup(DeskGroup group)
    {
        var owner = FindWindowForGroup(group.Id);
        if (owner is null || MessageBox.Show(owner, $"解散“{group.Name}”？\n文件仍保留在桌面，只会移除视觉归属。", "MiniC",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var paths = _layout.NativeDesktop.Assignments
            .Where(entry => entry.Value.Equals(group.Id, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key).ToArray();
        foreach (var path in paths) _layout.NativeDesktop.Assignments.Remove(path);
        BoxTopologyService.Remove(_boxHosts, new BoxTabRef(BoxContentKind.DesktopGroup, group.Id));
        Groups.Remove(group);
        SyncLegacyGroupTopology();
        EnsureGroupWindowCoverage();
        ScheduleSave();
    }

    private static BoxHostState CloneHost(BoxHostState host) => new()
    {
        Id = host.Id, Tabs = host.Tabs.ToList(), ActiveTab = host.ActiveTab,
        X = host.X, Y = host.Y, Width = host.Width, Height = host.Height,
        IsCollapsed = host.IsCollapsed, Theme = host.Theme, Material = host.Material,
        Opacity = host.Opacity, Appearance = host.Appearance
    };

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

    private static (string Theme, string Material, double Opacity) GetDefaultStyle() =>
        (BoxAppearanceDefaults.Theme, BoxAppearanceDefaults.Material, BoxAppearanceDefaults.Opacity);

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
            BoxTopologyService.Validate(_boxHosts,
                Groups.Select(group => new BoxTabRef(BoxContentKind.DesktopGroup, group.Id))
                    .Concat(_widgets.Keys.Select(id => new BoxTabRef(BoxContentKind.Widget, id))).ToArray());
            _layout.BoxHosts = _boxHosts;
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
        _wallpaperRefreshTimer.Stop();
        _widgetReminderTimer.Stop();
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
        _widgets.Clear();
        _shutdownApplication();
    }

    private IntPtr MessageWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == DisplayChangedMessage) GpuGlassRenderService.InvalidateOutputsIfRunning();
        if (message is SettingsChangedMessage or DisplayChangedMessage or DpiChangedMessage or ThemeChangedMessage)
            ScheduleWallpaperRefresh();
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
        _wallpaperRefreshTimer.Stop();
        _widgetReminderTimer.Stop();
        RestoreNativePositions(_layout.NativeDesktop.Assignments.Keys);
        RestoreNativePositions(DesktopItems.Where(item => !item.IsVirtual).Select(item => item.Path));
        _ = _nativeDesktopService.SetDesktopIconLayerVisible(true);
        _desktopFileService.DesktopChanged -= DesktopFileService_DesktopChanged;
        _desktopFileService.Dispose();
        foreach (var window in _windows.Values) window.ClosePermanently();
        _windows.Clear();
        _widgets.Clear();
        _desktopSurface?.ClosePermanently();
        _desktopSurface = null;
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
