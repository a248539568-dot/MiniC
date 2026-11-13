using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MiniC.Controls;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Models;
using MiniC.Widgets;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Separator = System.Windows.Controls.Separator;
using TextBox = System.Windows.Controls.TextBox;

namespace MiniC.Views;

/// <summary>只占用单个收纳盒或标签组矩形的桌面窗口。</summary>
public partial class DeskGroupWindow : DesktopBoxWindow
{
    public const string InternalPathsFormat = "MiniC.GroupPaths";
    private const int MinimumWidth = 250;
    private const int MinimumHeight = 170;
    private const double HeaderHeight = 42;
    private const double TabsHeight = 34;
    private const int GroupDragTransitionMilliseconds = 180;
    private const int GroupDragReleaseMilliseconds = 260;
    private const double GroupDragMinimumScale = 0.38;

    private readonly DeskGroupWindowCallbacks _callbacks;
    private readonly DesktopLayerHostService _desktopLayerHostService;
    private readonly DesktopDragStateService _desktopDragStateService;
    private readonly MarqueeSelectionController _marqueeSelection;
    private readonly DesktopItemOpenGestureTracker _openGestureTracker = new();
    private readonly ObservableCollection<DeskGroup> _tabMembers = [];
    private readonly ObservableCollection<BoxTabItem> _mixedTabs = [];
    private BoxTabRef? _mixedDragTab;
    private System.Windows.Point _mixedDragScreenStart;
    private TabDragMode _mixedDragMode;
    private Rect _mixedDragTabsBounds;
    private GroupDragPreviewWindow? _mixedTabPreview;
    private bool _mixedTabPreviewUnavailable;
    private ScaleTransform? _boxTransferScale;
    private Rect _boxDragSourceBounds;
    private Vector _mixedDragGrabOffset;
    private readonly Dictionary<string, WidgetContentView> _widgetContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WidgetState> _widgetStates = new(StringComparer.OrdinalIgnoreCase);
    private WidgetStore? _widgetStore;
    private WidgetWeatherService? _widgetWeather;
    private Action<WidgetKind>? _createWidget;
    private Action<WidgetState>? _deleteWidget;
    private Action? _hostStateChanged;
    private Action<BoxTabRef, BoxTabRef>? _mergeContent;
    private Action<BoxTabRef, BoxTabRef>? _mergeHost;
    private Func<BoxTabRef, System.Windows.Point, BoxTabRef?>? _findBoxMergeTarget;
    private Func<BoxTabRef, System.Windows.Point, BoxTabRef?>? _findWholeBoxMergeTarget;
    private Action<BoxTabRef, BoxTabRef?>? _setBoxMergeTarget;
    private Action<BoxTabRef, Rect, double>? _detachContent;
    private Action<BoxTabRef, IReadOnlyList<BoxTabRef>>? _reorderContent;
    private BoxHostState? _hostState;
    private BoxTabRef? _displayedTab;
    private readonly DeskGroupBoxContent _boxContent;
    private System.Windows.Point _dragStart;
    private System.Windows.Point _windowDragScreenStart;
    private System.Windows.Point _windowDragOrigin;
    private System.Windows.Point _windowDragPhysicalOrigin;
    private bool _returningBoxToOrigin;
    private int _boxReturnVersion;
    private bool _isMovingWindow;
    private bool _windowDragHasMoved;
    private bool _dragMoveRenderingSubscribed;
    private bool _pendingDragMove;
    private int _pendingDragX;
    private int _pendingDragY;
    private bool _boxTransferTargetReady;
    private bool _backdropAlignmentRenderingSubscribed;
    private readonly BoxMergeGesture _boxMergeGesture = new();
    private readonly DispatcherTimer _boxMergeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private BoxTabRef? _boxDragTarget;
    private bool _allowClose;
    private readonly InlineFileRenameSession _renameSession = new();
    private bool _transientPopupOpen;
    private ContextMenu? _groupMenu;
    private DesktopItem[] _draggedItems = [];
    private readonly ReorderSlotTracker _reorderSlots = new();
    private bool _mergeDropTargetActive;
    private bool _isApplyingCollapse;
    private bool _isApplyingAppearance;
    private HwndSource? _windowSource;
    private ComposedFrostedBackdrop? _composedFrostedBackdrop;
    private ComposedCrystalBackdrop? _composedCrystalBackdrop;
    private WindowCaptureExclusion? _captureExclusion;
    private bool _backdropUpdateQueued;
    private bool _windowClosed;
    private int _interactionVersion;

    public static readonly DependencyProperty IsItemDragActiveProperty = DependencyProperty.Register(
        nameof(IsItemDragActive), typeof(bool), typeof(DeskGroupWindow), new PropertyMetadata(false));

    public bool IsItemDragActive
    {
        get => (bool)GetValue(IsItemDragActiveProperty);
        private set => SetValue(IsItemDragActiveProperty, value);
    }

    public DeskGroup Group { get; private set; }
    public IBoxContent ActiveContent => ActiveWidget is not null && _hostState?.ActiveTab is { } tab
        && _widgetContents.TryGetValue(tab.Id, out var content) ? content : _boxContent;
    public BoxTabRef? ActiveTab => _hostState?.ActiveTab;
    public string ActiveTitle => ActiveWidget?.Title ?? Group.Name;
    public DeskGroup? ActiveGroup => ActiveTab?.Kind == BoxContentKind.DesktopGroup ? Group : null;
    public bool ContainsTab(BoxTabRef tab) => _hostState?.Tabs.Contains(tab) == true;
    public string HostId => _hostState?.Id ?? string.Empty;
    public nint DesktopHostHandle { get; private set; }

    public DeskGroupWindow(
        DeskGroup group,
        DeskGroupWindowCallbacks callbacks,
        DesktopLayerHostService desktopLayerHostService,
        DesktopDragStateService desktopDragStateService)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _desktopLayerHostService = desktopLayerHostService ?? throw new ArgumentNullException(nameof(desktopLayerHostService));
        InitializeDesktopBox(_desktopLayerHostService);
        _desktopDragStateService = desktopDragStateService
                                   ?? throw new ArgumentNullException(nameof(desktopDragStateService));
        _desktopDragStateService.Changed += DesktopDragStateService_Changed;
        IsItemDragActive = _desktopDragStateService.IsActive;
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _boxMergeTimer.Stop();
            StopPendingDraggedWindowMove();
            StopQueuedBackdropAlignment();
            ResetBoxTransferScale();
            ResetMixedTabDragPreview();
            Group.PropertyChanged -= GroupAppearanceChanged;
            _composedFrostedBackdrop?.Dispose();
            _composedFrostedBackdrop = null;
            _composedCrystalBackdrop?.Dispose();
            _composedCrystalBackdrop = null;
            _captureExclusion?.Dispose();
            _captureExclusion = null;
            Group.ComposedBackdropActive = false;
            _desktopDragStateService.Changed -= DesktopDragStateService_Changed;
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
            foreach (var content in _widgetContents.Values) content.Stop();
        };
        InitializeComponent();
        _boxMergeTimer.Tick += (_, _) => PollBoxMergeIntent();
        var fileSurface = ItemsScroller;
        if (fileSurface.Parent is not Grid fileHost)
            throw new InvalidOperationException("Group file surface must have a grid host.");
        var fileRow = Grid.GetRow(fileSurface);
        fileHost.Children.Remove(fileSurface);
        _boxContent = new DeskGroupBoxContent(group, fileSurface);
        Grid.SetRow(_boxContent, fileRow);
        fileHost.Children.Add(_boxContent);
        fileSurface.SizeChanged += (_, _) => UpdateFileSurfaceWidth(fileSurface);
        fileSurface.Loaded += (_, _) => UpdateFileSurfaceWidth(fileSurface);
        GroupMaterial.SizeChanged += (_, _) => UpdateMaterialClip();
        GroupShell.SizeChanged += (_, _) =>
        {
            if (!_mergeDropTargetActive) return;
            UpdateMergeDropOverlayBounds();
        };
        MergeDropOverlay.Loaded += (_, _) =>
        {
            if (_mergeDropTargetActive) UpdateMergeDropOverlayBounds();
        };
        _marqueeSelection = new MarqueeSelectionController(
            SelectionSurface, SelectionMarquee, () => Group.Items, FindItemTile);
        DataContext = Group;
        Left = Group.X;
        Top = Group.Y;
        Width = Math.Max(MinimumWidth, Group.Width);
        Height = Math.Max(MinimumHeight, Group.Height);
        _tabMembers.Add(group);
        SwitchGroup(group);
        LocationChanged += (_, _) =>
        {
            if (!_isMovingWindow && !_returningBoxToOrigin) SynchronizeBounds();
            QueueBackdropAlignment();
        };
        SizeChanged += (_, _) => { SynchronizeBounds(); UpdateMaterialClip(); QueueBackdropAlignment(); };
        IsVisibleChanged += (_, _) => UpdateBackdrop();
        Activated += (_, _) => QueueBackdropAlignment();
        Activated += (_, _) => { if (ActiveWidget is { Kind: WidgetKind.Calendar } && _hostState?.ActiveTab is { } tab)
            _widgetContents[tab.Id].RenderBody(); };
    }

    private WidgetState? ActiveWidget => _hostState?.ActiveTab is { Kind: BoxContentKind.Widget } tab
        ? _widgetStates.GetValueOrDefault(tab.Id) : null;

    private void UpdateFileSurfaceWidth(ScrollViewer fileSurface)
    {
        // ViewportWidth is the measured content width for this reparented ScrollViewer;
        // ActualWidth is the stable window-side width needed by the wrapping panel.
        var width = fileSurface.ActualWidth;
        if (double.IsFinite(width) && width > 0) SelectionSurface.Width = width;
    }

    public void ConfigureHost(BoxHostState host, IReadOnlyDictionary<string, DeskGroup> groups,
        IReadOnlyDictionary<string, WidgetState> widgets, WidgetStore store, WidgetWeatherService weather,
        Action<WidgetKind> createWidget, Action<WidgetState> deleteWidget, Action stateChanged,
        Action<BoxTabRef, BoxTabRef> merge, Action<BoxTabRef, BoxTabRef> mergeHost,
        Action<BoxTabRef, Rect, double> detach,
        Action<BoxTabRef, IReadOnlyList<BoxTabRef>> reorder,
        Func<BoxTabRef, System.Windows.Point, BoxTabRef?> findMergeTarget,
        Action<BoxTabRef, BoxTabRef?> setMergeTarget,
        Func<BoxTabRef, System.Windows.Point, BoxTabRef?>? findWholeBoxMergeTarget = null)
    {
        _hostState = host;
        _widgetStore = store;
        _widgetWeather = weather;
        _createWidget = createWidget;
        _deleteWidget = deleteWidget;
        _hostStateChanged = stateChanged;
        _mergeContent = merge;
        _mergeHost = mergeHost;
        _detachContent = detach;
        _reorderContent = reorder;
        _findBoxMergeTarget = findMergeTarget;
        _findWholeBoxMergeTarget = findWholeBoxMergeTarget;
        _setBoxMergeTarget = setMergeTarget;
        _widgetStates.Clear();
        foreach (var tab in host.Tabs.Where(tab => tab.Kind == BoxContentKind.Widget))
            if (widgets.TryGetValue(tab.Id, out var state)) _widgetStates[tab.Id] = state;
        foreach (var stale in _widgetContents.Keys.Except(_widgetStates.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (ReferenceEquals(WidgetHost.Content, _widgetContents[stale])) WidgetHost.Content = null;
            _widgetContents[stale].Stop();
            _widgetContents.Remove(stale);
        }
        var firstGroup = host.Tabs.Where(tab => tab.Kind == BoxContentKind.DesktopGroup)
            .Select(tab => groups.GetValueOrDefault(tab.Id)).FirstOrDefault(group => group is not null);
        var displayGroup = host.ActiveTab is { Kind: BoxContentKind.DesktopGroup } activeGroup
            ? groups.GetValueOrDefault(activeGroup.Id) ?? firstGroup : firstGroup;
        if (displayGroup is not null && !ReferenceEquals(Group, displayGroup))
        {
            SetGroupContext(displayGroup);
        }
        if (firstGroup is null && !Group.Id.Equals("host-anchor-" + host.Id, StringComparison.OrdinalIgnoreCase))
        {
            SetGroupContext(new DeskGroup { Id = "host-anchor-" + host.Id, Name = "组件" });
        }
        if (firstGroup is null && _widgetStates.Values.FirstOrDefault() is { } primaryWidget)
            Group.Name = primaryWidget.Title;
        var members = host.Tabs.Where(tab => tab.Kind == BoxContentKind.DesktopGroup)
            .Select(tab => groups[tab.Id]).ToArray();
        if (!_tabMembers.SequenceEqual(members))
        {
            _tabMembers.Clear();
            foreach (var member in members) _tabMembers.Add(member);
        }
        if (!_mixedTabs.Select(item => item.Tab).SequenceEqual(host.Tabs) ||
            _mixedTabs.Any(item => item.Title != (item.Tab.Kind == BoxContentKind.Widget
                ? _widgetStates[item.Tab.Id].Title : groups[item.Tab.Id].Name)))
        {
            if (_mixedDragTab is null)
            {
                _mixedTabs.Clear();
                foreach (var tab in host.Tabs)
                    _mixedTabs.Add(new BoxTabItem(tab, tab.Kind == BoxContentKind.Widget
                        ? _widgetStates[tab.Id].Title : groups[tab.Id].Name));
            }
        }
        TabsHost.ItemsSource = _mixedTabs;
        TabsBar.Visibility = host.Tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        GroupCollapseButton.ToolTip = host.IsCollapsed ? "展开" : "折叠";
        GroupTitleArea.Visibility = host.ActiveTab?.Kind == BoxContentKind.Widget
            ? Visibility.Collapsed : Visibility.Visible;
        WidgetTitle.Visibility = host.ActiveTab?.Kind == BoxContentKind.Widget
            ? Visibility.Visible : Visibility.Collapsed;
        var appearance = host.Appearance ?? firstGroup?.Appearance ?? _widgetStates.Values.FirstOrDefault()?.Appearance;
        host.Appearance ??= appearance ?? new MaterialPreferences();
            host.Theme ??= firstGroup?.Theme ?? _widgetStates.Values.FirstOrDefault()?.Theme ?? "White";
        host.Material ??= firstGroup?.Material ?? _widgetStates.Values.FirstOrDefault()?.Material ?? "Crystal";
            host.Opacity ??= firstGroup?.Opacity ?? _widgetStates.Values.FirstOrDefault()?.Opacity ?? 0.62;
        ApplyHostAppearance();
        Group.IsCollapsed = host.IsCollapsed;
        DataContext = Group;
        if (!_isMovingWindow && !_returningBoxToOrigin)
        {
            Left = host.X;
            Top = host.Y;
        }
        Width = Math.Max(MinimumWidth, host.Width);
        if (host.Tabs.Count == 1 && host.Tabs[0].Kind == BoxContentKind.Widget &&
            _widgetStates.TryGetValue(host.Tabs[0].Id, out var singleWidget) && singleWidget.Kind == WidgetKind.Calendar &&
            (host.Width > 380 || host.Height > 470))
        {
            host.Width = 340;
            host.Height = 380;
            Width = host.Width;
        }
        if (host.Width <= 0) host.Width = Width;
        if (host.Height <= 0) host.Height = Math.Max(MinimumHeight, firstGroup?.Height ?? _widgetStates.Values.FirstOrDefault()?.Height ?? MinimumHeight);
        if (!ReferenceEquals(_boxContent.DataContext, Group)) _boxContent.SetGroup(Group);
        SwitchTab(host.ActiveTab ?? host.Tabs[0]);
    }

    private void ApplyHostAppearance()
    {
        if (_hostState is null) return;
        _isApplyingAppearance = true;
        try { SetGroupAppearance(Group, ReadHostAppearance()); }
        finally { _isApplyingAppearance = false; }
        UpdateMaterialClip();
        UpdateBackdrop();
    }

    private WindowAppearanceService.State ReadHostAppearance() => WindowAppearanceService.Normalize(new(
        _hostState?.Theme ?? Group.Theme, _hostState?.Material ?? Group.Material,
        _hostState?.Opacity ?? Group.Opacity, _hostState?.Appearance ?? Group.Appearance));

    private static void SetGroupAppearance(DeskGroup group, WindowAppearanceService.State state)
    {
        group.Appearance = state.Appearance;
        group.Theme = state.Theme;
        group.Material = state.Material;
        group.Opacity = state.Opacity;
    }

    private void SetGroupContext(DeskGroup group)
    {
        Group.PropertyChanged -= GroupAppearanceChanged;
        Group.ComposedBackdropActive = false;
        Group = group;
        Group.PropertyChanged += GroupAppearanceChanged;
    }

    public void SwitchTab(BoxTabRef tab)
    {
        if (_hostState is null || !_hostState.Tabs.Contains(tab)) return;
        var changed = _displayedTab != tab;
        if (_displayedTab is { Kind: BoxContentKind.Widget } previous && previous != tab)
            _widgetContents.GetValueOrDefault(previous.Id)?.StopWeather();
        _hostState.ActiveTab = tab;
        GroupTitleArea.Visibility = tab.Kind == BoxContentKind.Widget ? Visibility.Collapsed : Visibility.Visible;
        WidgetTitle.Visibility = tab.Kind == BoxContentKind.Widget ? Visibility.Visible : Visibility.Collapsed;
        GroupItemCounter.Visibility = tab.Kind == BoxContentKind.Widget ? Visibility.Collapsed : Visibility.Visible;
        GroupViewModeButton.Visibility = tab.Kind == BoxContentKind.Widget ? Visibility.Collapsed : Visibility.Visible;
        if (tab.Kind == BoxContentKind.DesktopGroup)
        {
            var group = _tabMembers.First(member => member.Id == tab.Id);
            WidgetHost.Content = null;
            WidgetHost.Visibility = Visibility.Collapsed;
            _boxContent.Visibility = _hostState.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ItemsScroller.IsHitTestVisible = true;
            SwitchGroup(group);
        }
        else
        {
            var anchor = _tabMembers.FirstOrDefault();
            if (anchor is not null && !ReferenceEquals(Group, anchor))
            {
                SetGroupContext(anchor);
            }
            ApplyHostAppearance();
            foreach (var member in _tabMembers) member.IsActive = false;
            _boxContent.Visibility = Visibility.Collapsed;
            ItemsScroller.IsHitTestVisible = false;
            var widget = _widgetStates[tab.Id];
            WidgetTitle.Text = widget.Title;
            if (!_widgetContents.TryGetValue(tab.Id, out var content))
            {
                content = new WidgetContentView(widget, _widgetStore!, _widgetWeather!,
                    () => _widgetStore!.Save(widget), _createWidget!);
                _widgetContents[tab.Id] = content;
                content.RenderBody();
            }
            else if (changed && !ReferenceEquals(WidgetHost.Content, content)) content.Resume();
            WidgetHost.Content = content;
            WidgetHost.Visibility = _hostState.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            DataContext = Group;
            _boxContent.SetGroup(Group);
            ApplyCollapsedState(animate: false);
            UpdateBackdrop();
        }
        foreach (var item in _mixedTabs) item.IsActive = item.Tab == tab;
        _displayedTab = tab;
        if (changed) _hostStateChanged?.Invoke();
    }

    private void OpenWidgetMenu(WidgetState widget, UIElement target)
    {
        _groupMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menuStyle = (Style)FindResource("BoxContextMenu");
        var itemStyle = (Style)FindResource("BoxMenuItem");
        var separatorStyle = (Style)FindResource("BoxMenuSeparator");
        var menu = new ContextMenu { PlacementTarget = target, DataContext = Group,
            Style = menuStyle, ItemContainerStyle = itemStyle };
        _groupMenu = menu;
        MenuItem Item(string title, Action action)
        {
            var item = new MenuItem { Header = title, Style = itemStyle };
            item.Click += (_, _) =>
            {
                action();
                KeepActiveAfterMenuAction();
            };
            return item;
        }
        if (widget.Kind == WidgetKind.Weather)
        {
            menu.Items.Add(Item("刷新天气", () => _widgetContents[widget.Id].RenderBody()));
            menu.Items.Add(Item("根据 IP 重新定位", () => _widgetContents[widget.Id].ResetWeather()));
            menu.Items.Add(Item(widget.ViewMode == "Large" ? "切换到小窗模式" : "切换到大窗模式", () =>
            {
                widget.ViewMode = widget.ViewMode == "Large" ? "Small" : "Large";
                _widgetStore!.Save(widget);
                _widgetContents[widget.Id].RenderBody();
                Width = widget.ViewMode == "Large" ? 700 : 340;
                if (_hostState?.IsCollapsed == false) Height = widget.ViewMode == "Large" ? 500 : 370;
            }));
            menu.Items.Add(new Separator { Style = separatorStyle });
        }
        AppearanceMenuBuilder.AddTo(menu, new AppearanceMenuBuilder.Context(
            ReadHostAppearance, menuStyle, itemStyle, separatorStyle, ChangeHostAppearance), "颜色", "材质");
        menu.Items.Add(Item("重命名", () =>
        {
            var editor = new TextBox { Text = widget.Title, MinWidth = 160 };
            var rename = new ContextMenu { PlacementTarget = target, DataContext = Group };
            rename.Items.Add(editor);
            editor.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                if (!string.IsNullOrWhiteSpace(editor.Text))
                {
                    widget.Title = editor.Text.Trim();
                    _widgetStore!.Save(widget);
                    var index = _mixedTabs.ToList().FindIndex(item => item.Tab == new BoxTabRef(BoxContentKind.Widget, widget.Id));
                    if (index >= 0) _mixedTabs[index] = new BoxTabItem(_mixedTabs[index].Tab, widget.Title) { IsActive = true };
                    WidgetTitle.Text = widget.Title;
                    _hostStateChanged?.Invoke();
                }
                rename.IsOpen = false;
            };
            rename.Opened += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                editor.Focus();
                editor.SelectAll();
            }, DispatcherPriority.Input);
            rename.Closed += (_, _) =>
            {
                if (ReferenceEquals(_groupMenu, rename)) _groupMenu = null;
                _transientPopupOpen = false;
            };
            rename.IsOpen = true;
        }));
        menu.Items.Add(Item(_hostState?.IsCollapsed == true ? "展开" : "折叠", ToggleCollapsed));
        var merge = new MenuItem { Header = "合并到", Style = itemStyle };
        if (_hostState?.ActiveTab is { } active)
            foreach (var other in System.Windows.Application.Current.Windows.OfType<DeskGroupWindow>()
                .Where(other => other.IsVisible && other.HostId != HostId && other.ActiveTab is not null))
            {
                var destination = other.ActiveTab!;
                merge.Items.Add(Item(other.ActiveTitle,
                    () => _mergeContent?.Invoke(active, destination)));
            }
        merge.IsEnabled = merge.Items.Count > 0;
        menu.Items.Add(merge);
        if (_hostState?.Tabs.Count > 1 && _hostState.ActiveTab is { } selected)
            menu.Items.Add(Item("移出标签组", () => _detachContent?.Invoke(selected,
                GetDetachedPreviewBounds(PointToScreen(new System.Windows.Point(32, 32))), 1)));
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(Item("新建日程中心", () => _createWidget?.Invoke(WidgetKind.Planner)));
        menu.Items.Add(Item("删除组件与其内容", () =>
        {
            if (MessageBox.Show(this, "确定删除此组件及导入的附件吗？", "删除组件",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _deleteWidget?.Invoke(widget);
        }));
        menu.Opened += (_, _) => _transientPopupOpen = true;
        menu.Closed += (_, _) => OnBoxMenuClosed(menu);
        _transientPopupOpen = true;
        menu.IsOpen = true;
    }

    internal void ApplyAppearanceState(WindowAppearanceService.State state)
    {
        if (_hostState is null) return;
        state = WindowAppearanceService.Normalize(state);
        _hostState.Theme = state.Theme;
        _hostState.Material = state.Material;
        _hostState.Opacity = state.Opacity;
        _hostState.Appearance = state.Appearance;
        foreach (var group in _tabMembers.Where(group => !ReferenceEquals(group, Group)))
            SetGroupAppearance(group, state);
        foreach (var widget in _widgetStates.Values)
        {
            if (widget.Theme == state.Theme && widget.Material == state.Material
                && widget.Opacity == state.Opacity && widget.Appearance == state.Appearance) continue;
            widget.Theme = state.Theme;
            widget.Material = state.Material;
            widget.Opacity = state.Opacity;
            widget.Appearance = state.Appearance;
            _widgetStore?.Save(widget);
        }
        ApplyHostAppearance();
    }

    private void ChangeHostAppearance(WindowAppearanceService.State state)
    {
        ApplyAppearanceState(state);
        KeepActiveAfterMenuAction();
        _hostStateChanged?.Invoke();
    }

    private void KeepActiveAfterMenuAction()
    {
        if (!_windowClosed && IsLoaded) SetActivationEnabled(true, focusSelection: false);
    }

    public bool ContainsGroup(string groupId) =>
        _tabMembers.Any(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

    private void SwitchGroup(DeskGroup group)
    {
        if (!_tabMembers.Contains(group)) return;
        var tab = new BoxTabRef(BoxContentKind.DesktopGroup, group.Id);
        var changed = _displayedTab != tab;
        // ConfigureHost runs during background reconciliation too. Only a real tab
        // change may finish editing or clear the current selection.
        if (changed)
        {
            foreach (var item in _tabMembers.SelectMany(member => member.Items).Where(item => item.IsRenaming))
                _ = CommitRenameAsync(item, restoreFocus: false);
            foreach (var member in _tabMembers)
            {
                foreach (var item in member.Items) item.IsSelected = false;
                member.IsRenaming = false;
            }
        }
        foreach (var member in _tabMembers) member.IsActive = ReferenceEquals(member, group);
        SetGroupContext(group);
        if (_hostState is not null) _hostState.ActiveTab = new BoxTabRef(BoxContentKind.DesktopGroup, group.Id);
        if (_hostState is not null)
        {
            ApplyHostAppearance();
            Group.IsCollapsed = _hostState.IsCollapsed;
        }
        _boxContent.SetGroup(group);
        DataContext = group;
        if (_hostState is not null)
        {
            WidgetHost.Content = null;
            WidgetHost.Visibility = Visibility.Collapsed;
            foreach (var item in _mixedTabs) item.IsActive = item.Tab == _hostState.ActiveTab;
        }
        _displayedTab = tab;
        ApplyCollapsedState(animate: false);
        UpdateBackdrop();
        UpdateMaterialClip();
    }

    private void GroupAppearanceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_isApplyingAppearance) return;
        if (args.PropertyName is nameof(DeskGroup.Material) or nameof(DeskGroup.Theme) or nameof(DeskGroup.Opacity)
            or nameof(DeskGroup.Appearance))
        {
            UpdateBackdrop();
            if (args.PropertyName == nameof(DeskGroup.Appearance)) UpdateMaterialClip();
        }
    }

    private void UpdateMaterialClip()
    {
        if (_windowClosed || GroupMaterial.ActualWidth <= 0 || GroupMaterial.ActualHeight <= 0) return;
        var radius = Math.Clamp(Group.Appearance.Radius, 0,
            Math.Min(GroupMaterial.ActualWidth, GroupMaterial.ActualHeight) / 2);
        // Border.CornerRadius paints only its own background. It does not clip
        // BlurEffect children, so the final mask must be on their parent.
        GroupMaterial.Clip = new RectangleGeometry(
            new Rect(0, 0, GroupMaterial.ActualWidth, GroupMaterial.ActualHeight), radius, radius);
        GroupShell.Clip = new RectangleGeometry(
            new Rect(0, 0, GroupShell.ActualWidth, GroupShell.ActualHeight), radius, radius);
    }

    private void UpdateBackdrop()
    {
        if (_windowClosed || !IsLoaded) return;
        if (!IsVisible)
        {
            UpdateBackdropCore();
            return;
        }
        if (_backdropUpdateQueued) return;
        _backdropUpdateQueued = true;
        // Material notifications also update visibility and source bindings.
        // Wait for them before attempting to attach a native background.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _backdropUpdateQueued = false;
            if (!_windowClosed && IsLoaded) UpdateBackdropCore();
        }));
    }

    private void UpdateBackdropCore()
    {
        // Capture visibility is a window policy, independent of material and background source.
        if (IsVisible && (Group.Appearance.HideFromCapture || (Group.IsCrystal && !Group.UsesWallpaper
            && (_composedCrystalBackdrop is null || !_composedCrystalBackdrop.CapturePolicyReady
                || _composedCrystalBackdrop.HideFromCapture != Group.Appearance.HideFromCapture))))
        {
            if (_captureExclusion is null)
            {
                try { _captureExclusion = new WindowCaptureExclusion(new WindowInteropHelper(this).Handle); }
                catch (System.ComponentModel.Win32Exception error)
                { MiniCLogger.Warning(nameof(DeskGroupWindow), $"Capture policy unavailable: {error.Message}"); }
            }
        }
        // The GPU callback confirms presentation, rather than merely registration of the new source.
        try { UpdateMaterialBackdropsCore(); }
        finally
        {
            if (!IsVisible || (!Group.Appearance.HideFromCapture
                && (_composedCrystalBackdrop is null || _composedCrystalBackdrop.CapturePolicyReady)))
            { _captureExclusion?.Dispose(); _captureExclusion = null; }
        }
    }

    private void UpdateMaterialBackdropsCore()
    {
        if (IsVisible && Group.IsCrystal && !Group.UsesWallpaper)
        {
            _composedCrystalBackdrop ??= ComposedCrystalBackdrop.TryCreate(this, CrystalBackdrop,
                Group.Appearance.HideFromCapture, UpdateBackdrop);
            if (_composedCrystalBackdrop is not null && !_composedCrystalBackdrop.SetHideFromCapture(Group.Appearance.HideFromCapture))
            {
                _composedCrystalBackdrop.Dispose();
                _composedCrystalBackdrop = null;
            }
            if (_composedCrystalBackdrop is not null)
            {
                _composedCrystalBackdrop.ForegroundOpacity = Opacity;
                _composedCrystalBackdrop.Align();
            }
        }
        else
        {
            _composedCrystalBackdrop?.Dispose();
            _composedCrystalBackdrop = null;
        }
        var enabled = IsVisible && Group.IsFrostedSeeThrough;
        if (enabled)
        {
            _composedFrostedBackdrop ??= ComposedFrostedBackdrop.TryCreate(this, FrostedBackdrop,
                Group.Appearance.Radius, Group.Appearance.FrostedBlurRadius,
                Group.Appearance.HideFromCapture);
            if (_composedFrostedBackdrop is not null && !_composedFrostedBackdrop.SetHideFromCapture(Group.Appearance.HideFromCapture))
            {
                _composedFrostedBackdrop.Dispose();
                _composedFrostedBackdrop = null;
            }
            if (_composedFrostedBackdrop is not null)
            {
                _composedFrostedBackdrop.Radius = Group.Appearance.Radius;
                _composedFrostedBackdrop.BlurRadius = Group.Appearance.FrostedBlurRadius;
                if (_composedFrostedBackdrop.Align())
                {
                    Group.ComposedBackdropActive = true;
                    return;
                }
                _composedFrostedBackdrop.Dispose();
                _composedFrostedBackdrop = null;
            }
        }
        _composedFrostedBackdrop?.Dispose();
        _composedFrostedBackdrop = null;
        Group.ComposedBackdropActive = false;
    }

    private void AlignBackdrops()
    {
        _composedCrystalBackdrop?.Align();
        _composedFrostedBackdrop?.Align();
        CrystalBackdrop.InvalidateBackdrop();
        FrostedBackdrop.InvalidateBackdrop();
        SolidBackdrop.InvalidateBackdrop();
    }

    private void QueueBackdropAlignment()
    {
        if (_windowClosed || _backdropAlignmentRenderingSubscribed) return;
        _backdropAlignmentRenderingSubscribed = true;
        CompositionTarget.Rendering += RenderQueuedBackdropAlignment;
    }

    private void RenderQueuedBackdropAlignment(object? sender, EventArgs e)
    {
        StopQueuedBackdropAlignment();
        if (!_windowClosed) AlignBackdrops();
    }

    private void StopQueuedBackdropAlignment()
    {
        if (!_backdropAlignmentRenderingSubscribed) return;
        CompositionTarget.Rendering -= RenderQueuedBackdropAlignment;
        _backdropAlignmentRenderingSubscribed = false;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property != OpacityProperty) return;
        if (_composedCrystalBackdrop is not null) _composedCrystalBackdrop.ForegroundOpacity = Opacity;
        if (_composedFrostedBackdrop is not null) _composedFrostedBackdrop.ForegroundOpacity = Opacity;
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    public void EnsureDesktopPlacement() => EnsureDesktopPlacement(force: false);

    private void EnsureDesktopPlacement(bool force)
    {
        if (!IsLoaded || IsActive || _isMovingWindow) return;
        var result = PlaceAboveDesktop(force);
        DesktopHostHandle = result.IsAttached ? result.HostHandle : 0;
        AlignBackdrops();
    }

    public void RefreshDesktopPlacement() => EnsureDesktopPlacement();

    public void BeginRename(DesktopItem item)
    {
        if (!Group.Items.Contains(item) || _renameSession.IsCommitting(item)) return;
        _openGestureTracker.Reset();
        foreach (var candidate in Group.Items.Where(candidate => candidate.IsRenaming && !ReferenceEquals(candidate, item)))
            _ = CommitRenameAsync(candidate, restoreFocus: false);
        SetActivationEnabled(true);
        item.EditName = DesktopFileService.GetRenameEditName(item);
        item.IsRenaming = true;
        Dispatcher.BeginInvoke(() =>
        {
            var tile = FindItemTile(item);
            var editor = tile is null ? null : FindVisualChild<TextBox>(tile);
            if (editor is null) return;
            editor.Focus();
            editor.Select(0, DesktopFileService.GetRenameSelectionLength(item, editor.Text));
        }, DispatcherPriority.Input);
    }

    public void SetActivationEnabled(bool enabled, bool focusSelection = true)
    {
        if (!IsLoaded) return;
        var version = ++_interactionVersion;
        _ = SetDesktopBoxActivation(enabled, () => EnsureDesktopPlacement(force: true));
        if (enabled && focusSelection) Dispatcher.BeginInvoke(() =>
        {
            if (version == _interactionVersion && IsActive && !Group.IsRenaming
                && !Group.Items.Any(item => item.IsRenaming)) Keyboard.Focus(SelectionSurface);
        }, DispatcherPriority.Input);
    }

    public void SetTransientPopupOpen(bool open) => _transientPopupOpen = open;

    protected override void ActivateDesktopBoxFromPointer() => SetActivationEnabled(true, focusSelection: false);

    public void SetMergeDropTarget(bool active)
    {
        if (_mergeDropTargetActive == active) return;
        _mergeDropTargetActive = active;
        UpdateMergeDropOverlayBounds();
        AnimateDouble(MergeDropOverlay, OpacityProperty, active ? 1 : 0, GroupDragTransitionMilliseconds);
    }

    private void UpdateMergeDropOverlayBounds()
    {
        var center = BoxDropGeometry.Center(new Rect(0, 0, GroupShell.ActualWidth, GroupShell.ActualHeight));
        var grid = GroupShell.Child as Grid;
        var offset = grid is null || !GroupShell.IsLoaded
            ? new System.Windows.Point() : GroupShell.TranslatePoint(new System.Windows.Point(), grid);
        var point = new System.Windows.Point(center.Left + offset.X, center.Top + offset.Y);
        if (grid is not null && IsLoaded) point = grid.TranslatePoint(point, MergeDropOverlay.Parent as UIElement ?? grid);
        MergeDropOverlay.Margin = new Thickness(point.X, point.Y, 0, 0);
        MergeDropOverlay.Width = center.Width;
        MergeDropOverlay.Height = center.Height;
    }

    public Rect GetMergeDropScreenBounds()
    {
        var region = GroupShell;
        if (!IsVisible || !region.IsVisible || region.ActualWidth <= 0 || region.ActualHeight <= 0)
            return Rect.Empty;
        // Use the direct content's layout offset; RenderTransform must not shrink the drop zone.
        var origin = VisualTreeHelper.GetOffset(region);
        return new Rect(PointToScreen(new System.Windows.Point(origin.X, origin.Y)),
            PointToScreen(new System.Windows.Point(origin.X + region.ActualWidth,
                origin.Y + region.ActualHeight)));
    }

    private Rect GetElementScreenBounds(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return Rect.Empty;
        var origin = element.TransformToAncestor(this).Transform(new System.Windows.Point());
        return new Rect(PointToScreen(origin), PointToScreen(new System.Windows.Point(
            origin.X + element.ActualWidth, origin.Y + element.ActualHeight)));
    }

    public void PrepareBoxTransferArrival(double startScale)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var scale = new ScaleTransform(Math.Clamp(startScale, GroupDragMinimumScale, 1),
            Math.Clamp(startScale, GroupDragMinimumScale, 1));
        GroupShell.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        GroupShell.RenderTransform = scale;
    }

    public void PlayBoxTransferArrival()
    {
        if (!IsLoaded || !IsVisible || _windowClosed || !SystemParameters.ClientAreaAnimation) return;
        if (GroupShell.RenderTransform is not ScaleTransform scale) return;
        var duration = GroupDragReleaseMilliseconds;
        AnimateTransformDouble(scale, ScaleTransform.ScaleXProperty, 1, duration);
        AnimateTransformDouble(scale, ScaleTransform.ScaleYProperty, 1, duration);
    }

    public void ClearTransientState(bool commitRename = true)
    {
        StopPendingDraggedWindowMove();
        _composedCrystalBackdrop?.FinishMove();
        if (_returningBoxToOrigin) CancelBoxReturnAnimation();
        _openGestureTracker.Reset();
        _marqueeSelection.Cancel();
        foreach (var item in _tabMembers.SelectMany(group => group.Items))
        {
            item.IsSelected = false;
            item.IsDropTarget = false;
            if (commitRename && item.IsRenaming && !_renameSession.IsCommitting(item))
                _ = CommitRenameAsync(item, restoreFocus: false);
        }
        foreach (var member in _tabMembers) member.IsRenaming = false;
        if (_groupMenu is not null) _groupMenu.IsOpen = false;
        if (_boxDragTarget is not null && _hostState?.ActiveTab is { } activeTab)
            SetBoxDragTarget(_mixedDragTab ?? activeTab, null);
        ResetBoxTransferScale();
        ResetMixedTabDragPreview();
        _boxDragSourceBounds = Rect.Empty;
        ResetBoxMergeIntent();
        _isMovingWindow = false;
        _mixedDragTab = null;
        _mixedDragMode = TabDragMode.None;
        _mixedDragTabsBounds = Rect.Empty;
        UpdateSelectionDisplay();
        SetActivationEnabled(false);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _ = _desktopLayerHostService.ConfigureAsDesktopToolWindow(handle, preventActivation: false);
        UpdateBackdrop();
        EnsureDesktopPlacement();
        AlignBackdrops();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x001A or 0x031E)
            Dispatcher.BeginInvoke(UpdateBackdrop);
        return IntPtr.Zero;
    }

    public async Task RefreshWallpaperAsync()
    {
        if (_windowClosed || !IsVisible) return;
        await Task.WhenAll(CrystalBackdrop.RefreshAsync(), FrostedBackdrop.RefreshAsync(), SolidBackdrop.RefreshAsync());
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        var version = _interactionVersion;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsEditingRename()) return;
            if (version == _interactionVersion && !_transientPopupOpen && !IsActive
                && !_isMovingWindow && _mixedDragTab is null)
                ClearTransientState();
        }, DispatcherPriority.Background);
    }

    private void SelectionSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || IsInsideTaggedElement(e.OriginalSource as DependencyObject, "DeskItemTile")) return;
        SetActivationEnabled(true);
        _openGestureTracker.Reset();
        _marqueeSelection.Begin(e.GetPosition(SelectionSurface),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        UpdateSelectionDisplay();
        e.Handled = true;
    }

    private void SelectionSurface_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_marqueeSelection.IsActive) return;
        if (e.LeftButton == MouseButtonState.Pressed)
            _marqueeSelection.Update(e.GetPosition(SelectionSurface));
        else
            _marqueeSelection.Complete(e.GetPosition(SelectionSurface));
        UpdateSelectionDisplay();
        e.Handled = true;
    }

    private void SelectionSurface_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeSelection.IsActive) return;
        _marqueeSelection.Complete(e.GetPosition(SelectionSurface));
        UpdateSelectionDisplay();
        e.Handled = true;
    }

    private async void SelectionSurface_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideTaggedElement(e.OriginalSource as DependencyObject, "DeskItemTile")) return;
        _callbacks.InteractionStarted(Group);
        var point = PointToScreen(e.GetPosition(this));
        e.Handled = true;
        await _callbacks.ShowBackgroundMenuAsync(Group,
            new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y)));
    }

    private void SelectionSurface_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _marqueeSelection.Cancel();
        UpdateSelectionDisplay();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 控件自己完成按下、捕获、松开和 Click，不在其预览阶段改焦点或启动标题拖动。
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (!ShouldStartHeaderDrag(e.OriginalSource as DependencyObject, e.ClickCount))
        {
            SetActivationEnabled(true);
            return;
        }
        if (e.ClickCount == 2)
        {
            SetActivationEnabled(true);
            ToggleCollapsed();
            e.Handled = true;
            return;
        }
        if (_returningBoxToOrigin) CancelBoxReturnAnimation();
        _isMovingWindow = true;
        _windowDragHasMoved = false;
        _windowDragScreenStart = PointToScreen(e.GetPosition(this));
        BeginBoxMergeIntent(_hostState?.ActiveTab ?? new BoxTabRef(BoxContentKind.DesktopGroup, Group.Id),
            _windowDragScreenStart);
        _windowDragOrigin = new System.Windows.Point(Left, Top);
        if (NativeWindowPlacement.TryGetBounds(new WindowInteropHelper(this).Handle, out var nativeBounds))
            _windowDragPhysicalOrigin = nativeBounds.TopLeft;
        else _windowDragPhysicalOrigin = PointToScreen(new System.Windows.Point());
        SetActivationEnabled(true, focusSelection: false);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private static bool ShouldStartHeaderDrag(DependencyObject? originalSource, int clickCount)
    {
        if (FindAncestor<Button>(originalSource) is not null
            || FindAncestor<TextBox>(originalSource) is not null) return false;
        return clickCount != 2 || !IsInsideTaggedElement(originalSource, "GroupTitle");
    }

    private void Header_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isMovingWindow || e.LeftButton != MouseButtonState.Pressed) return;
        var cursor = System.Windows.Forms.Cursor.Position;
        var current = new System.Windows.Point(cursor.X, cursor.Y);
        if (!_windowDragHasMoved)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (Math.Abs(current.X - _windowDragScreenStart.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
                && Math.Abs(current.Y - _windowDragScreenStart.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
            _windowDragHasMoved = true;
        }
        var source = _hostState?.ActiveTab ?? new BoxTabRef(BoxContentKind.DesktopGroup, Group.Id);
        _boxMergeTimer.Start();
        var destination = UpdateBoxMergeIntent(source, current);
        MoveDraggedWindow(current, snap: destination is null);
        e.Handled = true;
    }

    private void MoveDraggedWindow(System.Windows.Point pointer, bool snap)
    {
        // Always derive from the original grab point, so leaving a snap never accumulates offsets.
        var x = _windowDragPhysicalOrigin.X + pointer.X - _windowDragScreenStart.X;
        var y = _windowDragPhysicalOrigin.Y + pointer.Y - _windowDragScreenStart.Y;
        var handle = new WindowInteropHelper(this).Handle;
        if (snap && _callbacks.SnapBounds is { } align && NativeWindowPlacement.TryGetBounds(handle, out var bounds))
        {
            var aligned = align(new Rect(x, y, bounds.Width, bounds.Height));
            x = aligned.X;
            y = aligned.Y;
        }
        _pendingDragX = (int)Math.Round(x);
        _pendingDragY = (int)Math.Round(y);
        _pendingDragMove = true;
        if (_dragMoveRenderingSubscribed) return;
        _dragMoveRenderingSubscribed = true;
        CompositionTarget.Rendering += RenderDraggedWindowMove;
    }

    private void RenderDraggedWindowMove(object? sender, EventArgs e)
    {
        if (!_pendingDragMove)
        {
            StopPendingDraggedWindowMove();
            return;
        }
        var x = _pendingDragX;
        var y = _pendingDragY;
        _pendingDragMove = false;
        ApplyDraggedWindowMove(x, y);
        QueueBackdropAlignment();
        if (!_pendingDragMove) StopPendingDraggedWindowMove();
    }

    private void FlushDraggedWindowMove()
    {
        if (_pendingDragMove)
        {
            _pendingDragMove = false;
            ApplyDraggedWindowMove(_pendingDragX, _pendingDragY);
        }
        StopPendingDraggedWindowMove();
        QueueBackdropAlignment();
    }

    private void ApplyDraggedWindowMove(int x, int y)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_composedCrystalBackdrop?.RequestMove(x, y) != true)
            NativeWindowPlacement.Move(handle, x, y);
    }

    private void StopPendingDraggedWindowMove()
    {
        if (_dragMoveRenderingSubscribed)
            CompositionTarget.Rendering -= RenderDraggedWindowMove;
        _dragMoveRenderingSubscribed = false;
        _pendingDragMove = false;
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMovingWindow) return;
        var cursor = System.Windows.Forms.Cursor.Position;
        var pointer = new System.Windows.Point(cursor.X, cursor.Y);
        var source = _hostState?.ActiveTab ?? new BoxTabRef(BoxContentKind.DesktopGroup, Group.Id);
        var destination = _windowDragHasMoved ? GetReadyBoxMergeTarget(source, pointer) : null;
        if (_windowDragHasMoved) MoveDraggedWindow(pointer, snap: destination is null);
        FlushDraggedWindowMove();
        _composedCrystalBackdrop?.FinishMove();
        _isMovingWindow = false;
        ResetBoxMergeIntent();
        ((UIElement)sender).ReleaseMouseCapture();
        SetBoxDragTarget(source, null);
        if (destination is not null)
        {
            e.Handled = true;
            SynchronizeBounds();
            _mergeHost?.Invoke(source, destination);
            ResetBoxTransferScale();
            return;
        }
        SynchronizeBounds();
        AnimateBoxTransferReset();
        e.Handled = true;
    }

    private void Header_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isMovingWindow) return;
        FlushDraggedWindowMove();
        _composedCrystalBackdrop?.FinishMove();
        _isMovingWindow = false;
        ResetBoxMergeIntent();
        SetBoxDragTarget(_hostState?.ActiveTab ?? new BoxTabRef(BoxContentKind.DesktopGroup, Group.Id), null);
        SynchronizeBounds();
        AnimateBoxTransferReset();
    }

    private void ReturnBoxToDragOrigin()
    {
        if (!SystemParameters.ClientAreaAnimation || _windowClosed)
        {
            _returningBoxToOrigin = true;
            Left = _windowDragOrigin.X;
            Top = _windowDragOrigin.Y;
            _returningBoxToOrigin = false;
            SynchronizeBounds();
            return;
        }
        var origin = _windowDragOrigin;
        var gesture = ++_boxReturnVersion;
        _returningBoxToOrigin = true;
        var x = new DoubleAnimation(origin.X, TimeSpan.FromMilliseconds(GroupDragReleaseMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        var y = new DoubleAnimation(origin.Y, TimeSpan.FromMilliseconds(GroupDragReleaseMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        y.Completed += (_, _) =>
        {
            if (!_returningBoxToOrigin || gesture != _boxReturnVersion) return;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = origin.X;
            Top = origin.Y;
            _returningBoxToOrigin = false;
            SynchronizeBounds();
        };
        BeginAnimation(LeftProperty, x);
        BeginAnimation(TopProperty, y);
    }

    private void CancelBoxReturnAnimation()
    {
        if (!_returningBoxToOrigin) return;
        var current = new System.Windows.Point(Left, Top);
        ++_boxReturnVersion;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = current.X;
        Top = current.Y;
        _returningBoxToOrigin = false;
        SynchronizeBounds();
    }

    private void SetBoxDragTarget(BoxTabRef source, BoxTabRef? target)
    {
        if (_boxDragTarget == target) return;
        _boxDragTarget = target;
        _setBoxMergeTarget?.Invoke(source, target);
    }

    private void BeginBoxMergeIntent(BoxTabRef source, System.Windows.Point pointer)
    {
        _boxMergeTimer.Stop();
        _boxMergeGesture.Reset(FindMergeTarget(source, pointer));
        SetBoxDragTarget(source, null);
    }

    private void ResetBoxMergeIntent()
    {
        _boxMergeTimer.Stop();
        _boxMergeGesture.Reset();
    }

    private BoxTabRef? UpdateBoxMergeIntent(BoxTabRef source, System.Windows.Point pointer)
    {
        var target = _boxMergeGesture.Update(FindMergeTarget(source, pointer), pointer,
            Environment.TickCount64, VisualTreeHelper.GetDpi(this).DpiScaleX);
        SetBoxDragTarget(source, target);
        UpdateBoxTransferScale(target is not null);
        return target;
    }

    private BoxTabRef? GetReadyBoxMergeTarget(BoxTabRef source, System.Windows.Point pointer) =>
        _boxDragTarget is { } ready && FindMergeTarget(source, pointer) == ready ? ready : null;

    private BoxTabRef? FindMergeTarget(BoxTabRef source, System.Windows.Point pointer) =>
        _isMovingWindow
            ? (_findWholeBoxMergeTarget ?? _findBoxMergeTarget)?.Invoke(source, pointer)
            : _findBoxMergeTarget?.Invoke(source, pointer);

    private void PollBoxMergeIntent()
    {
        var source = _isMovingWindow && _windowDragHasMoved ? _hostState?.ActiveTab
            : _mixedDragMode == TabDragMode.Transfer ? _mixedDragTab : null;
        if (source is null) { _boxMergeTimer.Stop(); return; }
        var cursor = System.Windows.Forms.Cursor.Position;
        var pointer = new System.Windows.Point(cursor.X, cursor.Y);
        var target = UpdateBoxMergeIntent(source, pointer);
        if (_mixedDragMode == TabDragMode.Transfer) UpdateMixedTabDragPreview(pointer, target);
    }

    private void UpdateBoxTransferScale(bool ready)
    {
        if (!ready)
        {
            if (_boxTransferScale is null || !_boxTransferTargetReady) return;
            _boxTransferTargetReady = false;
            AnimateTransformDouble(_boxTransferScale, ScaleTransform.ScaleXProperty, 1, GroupDragTransitionMilliseconds);
            AnimateTransformDouble(_boxTransferScale, ScaleTransform.ScaleYProperty, 1, GroupDragTransitionMilliseconds);
            return;
        }
        if (!SystemParameters.ClientAreaAnimation) return;
        if (_boxTransferScale is null)
        {
            _boxTransferScale = new ScaleTransform(1, 1);
            _boxTransferScale.Changed += (_, _) => QueueBackdropAlignment();
            GroupShell.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            GroupShell.RenderTransform = _boxTransferScale;
        }
        if (!ReferenceEquals(GroupShell.RenderTransform, _boxTransferScale))
        {
            GroupShell.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            GroupShell.RenderTransform = _boxTransferScale;
        }
        if (_boxTransferTargetReady) return;
        _boxTransferTargetReady = true;
        AnimateTransformDouble(_boxTransferScale, ScaleTransform.ScaleXProperty,
            GroupDragMinimumScale, GroupDragTransitionMilliseconds);
        AnimateTransformDouble(_boxTransferScale, ScaleTransform.ScaleYProperty,
            GroupDragMinimumScale, GroupDragTransitionMilliseconds);
    }

    private void ResetBoxTransferScale()
    {
        if (_boxTransferScale is null) return;
        _boxTransferTargetReady = false;
        _boxTransferScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _boxTransferScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _boxTransferScale = null;
        if (!_windowClosed) GroupShell.RenderTransform = Transform.Identity;
        if (!_windowClosed) QueueBackdropAlignment();
    }

    private void AnimateBoxTransferReset()
    {
        UpdateBoxTransferScale(ready: false);
    }

    private void UpdateMixedTabDragPreview(System.Windows.Point pointer, BoxTabRef? target)
    {
        if (!TabDragPolicy.ShowDetachedPreview(_mixedDragMode, _mixedDragTabsBounds, pointer))
        {
            ResetMixedTabDragPreview();
            return;
        }
        if (!SystemParameters.ClientAreaAnimation || _windowClosed || _mixedTabPreviewUnavailable) return;
        var bounds = GetMixedTabPreviewBounds(pointer);
        var anchor = GroupDetachPlacement.Anchor(bounds, _mixedDragGrabOffset);
        var preview = _mixedTabPreview;
        if (preview is null)
        {
            try
            {
                preview = new GroupDragPreviewWindow(GroupShell,
                    new Rect(Left, Top, Width, Height), Group.GlassBackground,
                    TabsHeight, bounds.Size, ItemsScroller, GroupMaterial, GroupHeader);
                _mixedTabPreview = preview;
                preview.InitializePose(bounds.TopLeft, anchor, GroupDragMinimumScale);
                preview.Show();
            }
            catch (Exception error)
            {
                MiniCLogger.Warning(nameof(DeskGroupWindow), $"Tab drag preview unavailable: {error}");
                _mixedTabPreviewUnavailable = true;
                ResetMixedTabDragPreview();
                return;
            }
        }
        preview.Follow(bounds.TopLeft, visible: target is null, scale: 1, pointer: anchor);
    }

    private Rect GetMixedTabPreviewBounds(System.Windows.Point screenPoint)
    {
        var local = PointFromScreen(screenPoint);
        var pointer = new System.Windows.Point(Left + local.X, Top + local.Y);
        var size = new System.Windows.Size(Width, Height);
        return GroupDetachPlacement.GetBounds(pointer, size, _mixedDragGrabOffset,
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
    }

    private void ResetMixedTabDragPreview()
    {
        var preview = _mixedTabPreview;
        _mixedTabPreview = null;
        preview?.Close();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_hostState?.IsCollapsed ?? Group.IsCollapsed) return;
        Width = Math.Max(MinimumWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinimumHeight, Height + e.VerticalChange);
        SynchronizeBounds();
    }

    private void SynchronizeBounds()
    {
        if (!IsLoaded || _isApplyingCollapse) return;
        if (_hostState is not null)
        {
            _hostState.X = Left; _hostState.Y = Top; _hostState.Width = Width;
            if (!_hostState.IsCollapsed) _hostState.Height = Height;
            foreach (var widget in _widgetStates.Values)
            {
                widget.X = Left; widget.Y = Top; widget.Width = Width;
                if (!_hostState.IsCollapsed) widget.Height = Height;
                _widgetStore?.Save(widget);
            }
        }
        foreach (var member in _tabMembers)
        {
            member.X = Left;
            member.Y = Top;
            member.Width = Width;
            if (!member.IsCollapsed) member.Height = Height;
        }
        _callbacks.StateChanged(Group);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => ToggleCollapsed();

    private void ViewModeButton_Click(object sender, RoutedEventArgs e) =>
        SetViewMode(Group.ViewMode == "List" ? "Icons" : "List");

    internal void SetViewMode(string mode, bool animate = true)
    {
        var normalized = mode == "List" ? "List" : "Icons";
        if (Group.ViewMode == normalized) return;
        Group.ViewMode = normalized;
        _callbacks.StateChanged(Group);
        if (!animate) return;
        ItemsScroller.BeginAnimation(OpacityProperty, null);
        ItemsScroller.Opacity = 0;
        UpdateLayout();
        AnimateDouble(ItemsScroller, OpacityProperty, 1, 160);
    }

    private void ToggleCollapsed()
    {
        var collapsed = !(_hostState?.IsCollapsed ?? Group.IsCollapsed);
        if (_hostState is not null) _hostState.IsCollapsed = collapsed;
        GroupCollapseButton.ToolTip = collapsed ? "展开" : "折叠";
        if (collapsed && _hostState is not null && Height > HeaderHeight + TabsHeight)
            _hostState.Height = Height;
        foreach (var member in _tabMembers) member.IsCollapsed = collapsed;
        foreach (var widget in _widgetStates.Values) { widget.Collapsed = collapsed; _widgetStore?.Save(widget); }
        ApplyCollapsedState(animate: true);
        _callbacks.StateChanged(Group);
    }

    private void ApplyCollapsedState(bool animate)
    {
        var collapsed = _hostState?.IsCollapsed ?? Group.IsCollapsed;
        var targetHeight = collapsed
            ? HeaderHeight + ((_hostState?.Tabs.Count ?? _tabMembers.Count) > 1 ? TabsHeight : 0)
            : Math.Max(MinimumHeight, _hostState?.Height ?? Group.Height);
        ResizeHandle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (!animate)
        {
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            _boxContent.Visibility = collapsed || _hostState?.ActiveTab?.Kind == BoxContentKind.Widget
                ? Visibility.Collapsed : Visibility.Visible;
            if (_hostState?.ActiveTab?.Kind == BoxContentKind.Widget)
            {
                WidgetHost.BeginAnimation(OpacityProperty, null);
                WidgetHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }
            ItemsScroller.Opacity = collapsed ? 0 : 1;
            WidgetHost.Opacity = collapsed ? 0 : 1;
            return;
        }

        _isApplyingCollapse = true;
        if (!collapsed)
        {
            _boxContent.Visibility = _hostState?.ActiveTab?.Kind == BoxContentKind.Widget
                ? Visibility.Collapsed : Visibility.Visible;
            if (_hostState?.ActiveTab?.Kind == BoxContentKind.Widget)
            {
                WidgetHost.Visibility = Visibility.Visible;
            }
            var activeBody = _hostState?.ActiveTab?.Kind == BoxContentKind.Widget
                ? (UIElement)WidgetHost : ItemsScroller;
            activeBody.Opacity = 0;
            activeBody.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }
        var animation = new DoubleAnimation(targetHeight, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            if (collapsed)
            {
                _boxContent.Visibility = Visibility.Collapsed;
                WidgetHost.Visibility = Visibility.Collapsed;
                ItemsScroller.Opacity = 0;
                WidgetHost.Opacity = 0;
            }
            _isApplyingCollapse = false;
            EnsureDesktopPlacement();
        };
        BeginAnimation(HeightProperty, animation);
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_hostState is null || sender is not FrameworkElement { DataContext: BoxTabItem item } element) return;
        SwitchTab(item.Tab);
        SetActivationEnabled(true, focusSelection: false);
        _mixedDragTab = item.Tab;
        _mixedTabPreviewUnavailable = false;
        _mixedDragMode = TabDragMode.Pending;
        _mixedDragScreenStart = PointToScreen(e.GetPosition(this));
        BeginBoxMergeIntent(item.Tab, _mixedDragScreenStart);
        _boxDragSourceBounds = GetMergeDropScreenBounds();
        _mixedDragGrabOffset = e.GetPosition(this) - new System.Windows.Point();
        _mixedDragTabsBounds = GetElementScreenBounds(TabsBar);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void Tab_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_hostState is null || _mixedDragTab is not { } tab) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetBoxMergeIntent();
            SetBoxDragTarget(tab, null);
            _mixedDragTab = null;
            _mixedDragMode = TabDragMode.None;
            _mixedDragTabsBounds = Rect.Empty;
            _boxDragSourceBounds = Rect.Empty;
            ResetMixedTabDragPreview();
            ResetBoxTransferScale();
            ((UIElement)sender).ReleaseMouseCapture();
            return;
        }
        var cursor = System.Windows.Forms.Cursor.Position;
        var point = new System.Windows.Point(cursor.X, cursor.Y);
        var delta = point - _mixedDragScreenStart;
        _mixedDragMode = TabDragPolicy.ResolveMode(_mixedDragMode, delta);
        if (_mixedDragMode == TabDragMode.Pending) return;
        if (_mixedDragMode == TabDragMode.Transfer)
        {
            _boxMergeTimer.Start();
            var target = UpdateBoxMergeIntent(tab, point);
            UpdateMixedTabDragPreview(point, target);
        }
        else if (_mixedDragTabsBounds.Contains(point) && _mixedTabs.Count > 1)
        {
            var index = Math.Clamp((int)(e.GetPosition(TabsHost).X /
                Math.Max(1, TabsHost.ActualWidth / _mixedTabs.Count)), 0, _mixedTabs.Count - 1);
            var oldIndex = _mixedTabs.ToList().FindIndex(item => item.Tab == tab);
            if (oldIndex >= 0 && oldIndex != index)
            {
                _mixedTabs.Move(oldIndex, index);
                _reorderContent?.Invoke(tab, _mixedTabs.Select(item => item.Tab).ToArray());
            }
        }
        e.Handled = true;
    }

    private void Tab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_hostState is null || _mixedDragTab is not { } tab) return;
        var cursor = System.Windows.Forms.Cursor.Position;
        var point = new System.Windows.Point(cursor.X, cursor.Y);
        var mode = _mixedDragMode;
        var sourceBounds = _boxDragSourceBounds;
        var detachBounds = GetMixedTabPreviewBounds(point);
        var destination = mode == TabDragMode.Transfer ? GetReadyBoxMergeTarget(tab, point) : null;
        ResetBoxMergeIntent();
        _mixedDragTab = null;
        _mixedDragMode = TabDragMode.None;
        _mixedDragTabsBounds = Rect.Empty;
        _boxDragSourceBounds = Rect.Empty;
        SetBoxDragTarget(tab, null);
        if (sender is UIElement element && element.IsMouseCaptured) element.ReleaseMouseCapture();
        if (mode != TabDragMode.Transfer)
        {
            ResetMixedTabDragPreview();
            AnimateBoxTransferReset();
            return;
        }
        if (destination is not null)
        {
            ResetMixedTabDragPreview();
            _mergeContent?.Invoke(tab, destination);
            ResetBoxTransferScale();
        }
        else if (_hostState.Tabs.Count > 1 && !sourceBounds.Contains(point))
        {
            ResetMixedTabDragPreview();
            ResetBoxTransferScale();
            _detachContent?.Invoke(tab, detachBounds, 1);
        }
        else
        {
            ResetMixedTabDragPreview();
            AnimateBoxTransferReset();
        }
        e.Handled = true;
    }

    private void Tab_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ResetBoxMergeIntent();
        if (_mixedDragTab is { } tab) SetBoxDragTarget(tab, null);
        _mixedDragTab = null;
        _mixedDragMode = TabDragMode.None;
        _mixedDragTabsBounds = Rect.Empty;
        _boxDragSourceBounds = Rect.Empty;
        ResetMixedTabDragPreview();
        AnimateBoxTransferReset();
    }

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item }) return;
        if (item.IsRenaming) return;
        _dragStart = e.GetPosition(this);
        var screenPoint = PointToScreen(_dragStart);
        _openGestureTracker.RegisterMouseDown(item.Path,
            new System.Drawing.Point((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)), e.Timestamp);
        SetActivationEnabled(true);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) item.IsSelected = !item.IsSelected;
        else if (!item.IsSelected)
        {
            foreach (var candidate in Group.Items) candidate.IsSelected = ReferenceEquals(candidate, item);
        }
        UpdateSelectionDisplay();
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item }
            || item.IsRenaming
            || e.ChangedButton != MouseButton.Left) return;
        if (!_openGestureTracker.RegisterMouseUp(item.Path)) return;

        if (!DesktopItemOpenService.TryOpen(item.Path, out var error))
        {
            MessageBox.Show(this, $"无法打开该项目。\n\n{error}", "MiniC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private async void Item_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: DesktopItem item } source
            || item.IsRenaming || !_openGestureTracker.IsPressed(item.Path)) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        // 只有在此图标按下的手势可以开始拖动。
        _openGestureTracker.Reset();

        var selected = item.IsSelected
            ? Group.Items.Where(candidate => candidate.IsSelected)
                .OrderBy(candidate => ReferenceEquals(candidate, item) ? 0 : 1).ToArray()
            : [item];
        _draggedItems = selected;
        _reorderSlots.Reset();
        var paths = selected.Select(candidate => candidate.Path).ToArray();
        var data = new System.Windows.DataObject();
        data.SetData(InternalPathsFormat, paths);
        data.SetData(System.Windows.DataFormats.FileDrop, paths);
        System.Windows.DragDropEffects effect;
        _desktopDragStateService.SetActive(true);
        _callbacks.SetDesktopDropTargetActive(true);
        try
        {
            var offsets = GetDragPreviewOffsets(selected, item);
            using var sourceVisibility = new DragSourceVisibilityScope(selected);
            effect = DragPreviewWindow.Run(source, selected, data,
                System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move,
                offsets);
        }
        finally
        {
            ClearDropTargets();
            _desktopDragStateService.SetActive(false);
            _callbacks.SetDesktopDropTargetActive(false);
            _draggedItems = [];
            _reorderSlots.Reset();
        }
        var cursor = System.Windows.Forms.Cursor.Position;
        await _callbacks.ReleasePathsAsync(Group, paths,
            new System.Windows.Point(cursor.X, cursor.Y), effect);
        e.Handled = true;
    }

    private async void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item }) return;
        _callbacks.InteractionStarted(Group);
        if (!item.IsSelected)
        {
            foreach (var candidate in Group.Items) candidate.IsSelected = ReferenceEquals(candidate, item);
        }
        UpdateSelectionDisplay();
        var selected = Group.Items.Where(candidate => candidate.IsSelected).ToArray();
        var point = PointToScreen(e.GetPosition(this));
        await _callbacks.ShowItemMenuAsync(Group, selected, item,
            new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y)));
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_mixedDragTab is not null || _isMovingWindow))
        {
            StopPendingDraggedWindowMove();
            _composedCrystalBackdrop?.FinishMove(applyLatest: false);
            var cancelWindowMove = _isMovingWindow;
            if (_hostState?.ActiveTab is { } activeTab) SetBoxDragTarget(_mixedDragTab ?? activeTab, null);
            ResetBoxTransferScale();
            _mixedDragTab = null;
            _mixedDragMode = TabDragMode.None;
            _mixedDragTabsBounds = Rect.Empty;
            _boxDragSourceBounds = Rect.Empty;
            ResetMixedTabDragPreview();
            _isMovingWindow = false;
            ResetBoxMergeIntent();
            if (Mouse.Captured is UIElement captured) captured.ReleaseMouseCapture();
            if (cancelWindowMove) ReturnBoxToDragOrigin();
            e.Handled = true;
            return;
        }
        if (ActiveWidget is not null && !_isMovingWindow) return;
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.V)
        {
            _ = ShellContextMenuService.PasteToDesktop(System.Windows.Forms.Cursor.Position);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.A)
        {
            foreach (var item in Group.Items) item.IsSelected = true;
            UpdateSelectionDisplay();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            var selectedForDelete = Group.Items.Where(item => item.IsSelected && !item.IsVirtual).ToArray();
            if (selectedForDelete.Length > 0)
            {
                _ = ShellContextMenuService.InvokeVerb(
                    new WindowInteropHelper(this).Handle,
                    selectedForDelete.Select(item => item.Path), "delete", System.Windows.Forms.Cursor.Position);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Enter)
        {
            var selectedForOpen = Group.Items.Where(item => item.IsSelected && !item.IsVirtual).ToArray();
            if (selectedForOpen.Length == 1)
            {
                if (!DesktopItemOpenService.TryOpen(selectedForOpen[0].Path, out var error))
                    MessageBox.Show(this, $"无法打开该项目。\n\n{error}", "MiniC",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
                return;
            }
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && e.Key is Key.C or Key.X)
        {
            var clipboardSelected = Group.Items.Where(item => item.IsSelected && !item.IsVirtual).ToArray();
            if (clipboardSelected.Length > 0)
            {
                var point = System.Windows.Forms.Cursor.Position;
                var verb = e.Key == Key.X ? "cut" : "copy";
                _ = ShellContextMenuService.InvokeVerb(
                    new WindowInteropHelper(this).Handle,
                    clipboardSelected.Select(item => item.Path), verb, point);
                e.Handled = true;
                return;
            }
        }
        if (e.Key != Key.F2) return;
        var selectedForRename = Group.Items.Where(item => item.IsSelected).ToArray();
        if (selectedForRename.Length != 1) return;
        BeginRename(selectedForRename[0]);
        e.Handled = true;
    }

    protected override void OnDesktopBoxInteractionStarted(MouseButtonEventArgs e)
    {
        // Every visible part of the box activates on the same first press.
        // Keep the routed event and keyboard focus available to the clicked control.
        _callbacks.InteractionStarted(Group);
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        var item = _tabMembers.SelectMany(group => group.Items)
            .FirstOrDefault(candidate => candidate.IsRenaming);
        if (item is not null) _ = CommitRenameAsync(item, restoreFocus: false);
    }

    private async void RenameBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DesktopItem item }) return;
        if (e.Key == Key.Escape)
        {
            CancelRename(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(item);
        }
    }

    private async void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: DesktopItem item } editor
            && item.IsRenaming && !editor.IsKeyboardFocusWithin && !_renameSession.IsCommitting(item))
            await CommitRenameAsync(item, restoreFocus: false);
    }

    private void CancelRename(DesktopItem item)
    {
        if (_renameSession.Cancel(item)) Keyboard.Focus(SelectionSurface);
    }

    private async Task CommitRenameAsync(DesktopItem item, bool restoreFocus = true)
    {
        if (!item.IsRenaming) return;
        var owner = _tabMembers.FirstOrDefault(member => member.Items.Contains(item)) ?? Group;
        var succeeded = await _renameSession.CommitAsync(item,
            name => _callbacks.RenameItemAsync(owner, item, name), keepEditingOnFailure: restoreFocus);
        if (!succeeded && restoreFocus && item.IsRenaming && IsActive)
            FocusRenameEditor();
        if (succeeded && restoreFocus && IsActive && Keyboard.FocusedElement is TextBox { DataContext: DesktopItem focused }
            && ReferenceEquals(focused, item)) Keyboard.Focus(SelectionSurface);
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (ActiveWidget is not null) { e.Effects = System.Windows.DragDropEffects.None; e.Handled = true; return; }
        ClearDropTargets();
        if (TryReorderInternalDrag(e))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        e.Effects = TryGetDroppedPaths(e.Data, out var paths)
                    && paths.Any(path => !IsVirtualShellPath(path))
            ? ResolveDropEffect(e, paths)
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private bool TryReorderInternalDrag(System.Windows.DragEventArgs e)
    {
        if (_draggedItems.Length == 0
            || !e.Data.GetDataPresent(InternalPathsFormat)
            || !TryGetDroppedPaths(e.Data, out var paths)) return false;
        var pathSet = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_draggedItems.Any(item => !pathSet.Contains(item.Path))
            || _draggedItems.Any(item => !Group.Items.Contains(item))) return false;

        var targetIndex = FindItemTargetIndex(e.GetPosition(SelectionSurface));
        if (targetIndex < 0) return true;
        if (!CollectionReorderService.MoveBlockToIndex(Group.Items, _draggedItems, targetIndex)) return true;
        Group.ItemOrder = Group.Items.Select(item => Path.GetFileName(item.Path)).ToList();
        _callbacks.StateChanged(Group);
        return true;
    }

    private int FindItemTargetIndex(System.Windows.Point point)
    {
        ItemsHost.UpdateLayout();
        var entries = Group.Items.Select((item, index) =>
        {
            var tile = FindItemTile(item);
            var bounds = Rect.Empty;
            if (tile is null) return (Index: index, Bounds: bounds);
            try
            {
                bounds = ReorderSlotTracker.LayoutBounds(tile, SelectionSurface);
            }
            catch (InvalidOperationException)
            {
                // 布局刷新期间项目可能短暂脱离可视树，本轮忽略即可。
            }
            return (Index: index, Bounds: bounds);
        }).Where(entry => !entry.Bounds.IsEmpty).ToArray();
        return _reorderSlots.Select(entries, point, Group.ViewMode == "List");
    }

    private void Item_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem target }
            || !CanDropOnItem(target, e.Data, out var paths)) return;
        ClearDropTargets(target);
        target.IsDropTarget = true;
        e.Effects = ResolveDropEffect(e, paths);
        e.Handled = true;
    }

    private void Item_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DesktopItem item }) item.IsDropTarget = false;
    }

    private async void Item_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem target }
            || !CanDropOnItem(target, e.Data, out var paths)) return;
        var effect = ResolveDropEffect(e, paths);
        ClearDropTargets();
        e.Effects = effect;
        e.Handled = true;
        await _callbacks.DropOnItemAsync(target, paths, effect == System.Windows.DragDropEffects.Copy);
    }

    private bool CanDropOnItem(DesktopItem target, System.Windows.IDataObject data, out string[] paths)
    {
        paths = [];
        if (!target.IsDirectory
            || target.IsVirtual && !target.Path.Equals(
                DesktopFileService.RecycleBinShellPath, StringComparison.OrdinalIgnoreCase)
            || !TryGetDroppedPaths(data, out paths)) return false;
        return paths.Any(path => !path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)
                                 && !IsVirtualShellPath(path));
    }

    private void ClearDropTargets(DesktopItem? except = null)
    {
        foreach (var item in Group.Items.Where(item => !ReferenceEquals(item, except))) item.IsDropTarget = false;
    }

    private void DesktopDragStateService_Changed(bool active) => IsItemDragActive = active;

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (ActiveWidget is not null) { e.Effects = System.Windows.DragDropEffects.None; e.Handled = true; return; }
        if (!TryGetDroppedPaths(e.Data, out var paths)) return;
        paths = paths.Where(path => !IsVirtualShellPath(path)).ToArray();
        if (paths.Length == 0)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var effect = ResolveDropEffect(e, paths);
        e.Effects = effect;
        e.Handled = true;
        await _callbacks.CollectPathsAsync(Group, paths, effect == System.Windows.DragDropEffects.Copy);
    }

    private static System.Windows.DragDropEffects ResolveDropEffect(
        System.Windows.DragEventArgs e,
        IReadOnlyCollection<string>? knownPaths = null)
    {
        if (e.Data.GetDataPresent(InternalPathsFormat)) return System.Windows.DragDropEffects.Move;
        if (e.KeyStates.HasFlag(System.Windows.DragDropKeyStates.ControlKey)
            && e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Copy))
            return System.Windows.DragDropEffects.Copy;
        if (e.KeyStates.HasFlag(System.Windows.DragDropKeyStates.ShiftKey)
            && e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Move))
            return System.Windows.DragDropEffects.Move;

        var paths = knownPaths ?? (TryGetDroppedPaths(e.Data, out var droppedPaths) ? droppedPaths : []);
        var desktopRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var sameVolume = paths.Count > 0 && paths.All(path =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), desktopRoot, StringComparison.OrdinalIgnoreCase));
        if (sameVolume && e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Move))
            return System.Windows.DragDropEffects.Move;
        return e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Copy)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.Move;
    }

    private static bool TryGetDroppedPaths(System.Windows.IDataObject data, out string[] paths)
    {
        paths = data.GetData(InternalPathsFormat, false) as string[]
                ?? data.GetData(System.Windows.DataFormats.FileDrop, true) as string[]
                ?? [];
        paths = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length > 0;
    }

    private static bool IsVirtualShellPath(string path) =>
        path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase);

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveWidget is { } widget) { OpenWidgetMenu(widget, (UIElement)sender); return; }
        _groupMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menuStyle = (Style)FindResource("BoxContextMenu");
        var itemStyle = (Style)FindResource("BoxMenuItem");
        var separatorStyle = (Style)FindResource("BoxMenuSeparator");
        MenuItem CreateItem(object header) => new() { Header = header, Style = itemStyle, ItemContainerStyle = itemStyle };
        Separator CreateSeparator() => new() { Style = separatorStyle };
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender, DataContext = Group, Style = menuStyle,
            ItemContainerStyle = itemStyle
        };
        _groupMenu = menu;
        var rename = CreateItem("重命名收纳盒");
        rename.Click += (_, _) => BeginGroupRename();
        menu.Items.Add(rename);
        var iconView = CreateItem("图标模式"); iconView.IsCheckable = true; iconView.IsChecked = Group.ViewMode != "List";
        var listView = CreateItem("列表预览"); listView.IsCheckable = true; listView.IsChecked = Group.ViewMode == "List";
        iconView.Click += (_, _) => { SetViewMode("Icons"); KeepActiveAfterMenuAction(); };
        listView.Click += (_, _) => { SetViewMode("List"); KeepActiveAfterMenuAction(); };
        menu.Items.Add(iconView); menu.Items.Add(listView); menu.Items.Add(CreateSeparator());
        AppearanceMenuBuilder.AddTo(menu, new AppearanceMenuBuilder.Context(
            ReadHostAppearance, menuStyle, itemStyle, separatorStyle, ChangeHostAppearance), "颜色", "材质");
        var collapse = CreateItem(Group.IsCollapsed ? "展开收纳盒" : "折叠收纳盒");
        collapse.Click += (_, _) => { ToggleCollapsed(); KeepActiveAfterMenuAction(); };
        menu.Items.Add(collapse);
        var merge = CreateItem("合并到");
        foreach (var target in System.Windows.Application.Current.Windows.OfType<DeskGroupWindow>()
            .Where(window => window.IsVisible && window.HostId != HostId && window.ActiveTab is not null))
        {
            var destination = target.ActiveTab!;
            var child = CreateItem(target.ActiveTitle);
            child.Click += (_, _) =>
            {
                _mergeContent?.Invoke(new BoxTabRef(BoxContentKind.DesktopGroup, Group.Id), destination);
                KeepActiveAfterMenuAction();
            };
            merge.Items.Add(child);
        }
        merge.IsEnabled = merge.Items.Count > 0; menu.Items.Add(merge);
        if (_hostState?.Tabs.Count > 1)
        {
            var detach = CreateItem("移出标签组");
            detach.Click += (_, _) =>
            {
                _detachContent?.Invoke(new BoxTabRef(BoxContentKind.DesktopGroup, Group.Id),
                    GetDetachedPreviewBounds(PointToScreen(new System.Windows.Point(32, 32))), 1);
                KeepActiveAfterMenuAction();
            };
            menu.Items.Add(detach);
        }
        menu.Items.Add(CreateSeparator());
        var create = CreateItem("新建收纳盒");
        create.Click += (_, _) => { _callbacks.CreateGroup(Group); KeepActiveAfterMenuAction(); };
        menu.Items.Add(create);
        var dissolve = CreateItem("解散收纳盒");
        dissolve.Click += (_, _) => { _callbacks.DissolveGroup(Group); KeepActiveAfterMenuAction(); };
        menu.Items.Add(dissolve);
        menu.Opened += (_, _) => _transientPopupOpen = true;
        menu.Closed += (_, _) => OnBoxMenuClosed(menu);
        _transientPopupOpen = true; menu.IsOpen = true;
    }

    private void OnBoxMenuClosed(ContextMenu menu)
    {
        if (!ReferenceEquals(_groupMenu, menu)) return;
        _groupMenu = null;
        _transientPopupOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsEditingRename())
            {
                FocusRenameEditor();
                return;
            }
            if (!IsActive && !_isMovingWindow && _mixedDragTab is null)
                ClearTransientState();
            _callbacks.RestoreDesktopLayerPlacement();
        }, DispatcherPriority.ContextIdle);
    }

    private bool IsEditingRename() => Group.IsRenaming
        || _tabMembers.Any(member => member.Items.Any(item => item.IsRenaming));

    private void FocusRenameEditor()
    {
        if (Group.IsRenaming)
        {
            GroupTitleEditor.Focus();
            GroupTitleEditor.SelectAll();
            KeepActiveAfterMenuAction();
            return;
        }
        var item = _tabMembers.SelectMany(member => member.Items).FirstOrDefault(candidate => candidate.IsRenaming);
        if (item is null) return;
        var tile = FindItemTile(item);
        var editor = tile is null ? null : FindVisualChild<TextBox>(tile);
        if (editor is not null)
        {
            editor.Focus();
            editor.Select(0, DesktopFileService.GetRenameSelectionLength(item, editor.Text));
        }
        KeepActiveAfterMenuAction();
    }
    private void BeginGroupRename()
    {
        Group.EditName = Group.Name;
        Group.IsRenaming = true;
        SetActivationEnabled(true);
        Dispatcher.BeginInvoke(() =>
        {
            GroupTitleEditor.Focus();
            GroupTitleEditor.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void GroupTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ActiveWidget is not null) return;
        if (e.ClickCount != 2) return;
        BeginGroupRename();
        e.Handled = true;
    }

    private void GroupTitleEditor_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelGroupRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitGroupRename();
            e.Handled = true;
        }
    }

    private void GroupTitleEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Group.IsRenaming) CommitGroupRename();
    }

    private void CommitGroupRename()
    {
        if (!Group.IsRenaming) return;
        var name = Group.EditName.Trim();
        if (name.Length > 0) Group.Name = name;
        Group.IsRenaming = false;
        _callbacks.StateChanged(Group);
        KeepActiveAfterMenuAction();
    }

    private void CancelGroupRename()
    {
        Group.EditName = Group.Name;
        Group.IsRenaming = false;
        KeepActiveAfterMenuAction();
    }

    private void UpdateSelectionDisplay() => SelectionDisplayService.Update(Group.Items);

    private Rect GetDetachedPreviewBounds(System.Windows.Point screenPoint)
    {
        var local = PointFromScreen(screenPoint);
        var position = new System.Windows.Point(Left + local.X, Top + local.Y);
        return GroupDetachPlacement.GetBounds(position, new System.Windows.Size(Group.Width, Group.Height),
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
    }

    private static void AnimateDouble(
        UIElement target,
        DependencyProperty property,
        double value,
        int durationMilliseconds) =>
        target.BeginAnimation(property,
            new DoubleAnimation(value, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });

    private static void AnimateTransformDouble(
        Animatable target,
        DependencyProperty property,
        double value,
        int durationMilliseconds) =>
        target.BeginAnimation(property,
            new DoubleAnimation(value, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });

    private FrameworkElement? FindItemTile(DesktopItem item) =>
        FindVisualChildren<FrameworkElement>(ItemsHost)
            .FirstOrDefault(element => Equals(element.Tag, "DeskItemTile") && ReferenceEquals(element.DataContext, item));

    private IReadOnlyDictionary<DesktopItem, System.Windows.Point> GetDragPreviewOffsets(
        IReadOnlyList<DesktopItem> items,
        DesktopItem anchorItem)
    {
        var anchorTile = FindItemTile(anchorItem);
        if (anchorTile is null) return new Dictionary<DesktopItem, System.Windows.Point>();
        var origin = anchorTile.PointToScreen(new System.Windows.Point());
        return items.ToDictionary(item => item, item =>
        {
            var tile = FindItemTile(item);
            if (tile is null) return new System.Windows.Point();
            var point = tile.PointToScreen(new System.Windows.Point());
            return new System.Windows.Point(point.X - origin.X, point.Y - origin.Y);
        });
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static bool IsInsideTaggedElement(DependencyObject? current, string tag)
    {
        while (current is not null)
        {
            if (current is FrameworkElement element && Equals(element.Tag, tag)) return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in FindVisualChildren<T>(parent)) return child;
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
