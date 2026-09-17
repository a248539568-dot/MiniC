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
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Separator = System.Windows.Controls.Separator;
using TextBox = System.Windows.Controls.TextBox;

namespace MiniC.Views;

/// <summary>只占用单个收纳盒或标签组矩形的桌面窗口。</summary>
public partial class DeskGroupWindow : Window
{
    private const int MouseActivateMessage = 0x0021;
    private static readonly IntPtr MouseActivate = new(1);
    public const string InternalPathsFormat = "MiniC.GroupPaths";
    private const int MinimumWidth = 250;
    private const int MinimumHeight = 170;
    private const double HeaderHeight = 42;
    private const double TabsHeight = 34;
    private const int GroupDragTransitionMilliseconds = 220;
    private const int GroupDragReleaseMilliseconds = 260;

    private readonly DeskGroupWindowCallbacks _callbacks;
    private readonly DesktopLayerHostService _desktopLayerHostService;
    private readonly DesktopDragStateService _desktopDragStateService;
    private readonly MarqueeSelectionController _marqueeSelection;
    private readonly DesktopItemOpenGestureTracker _openGestureTracker = new();
    private readonly ObservableCollection<DeskGroup> _tabMembers = [];
    private System.Windows.Point _dragStart;
    private System.Windows.Point _windowDragScreenStart;
    private System.Windows.Point _windowDragOrigin;
    private bool _isMovingWindow;
    private bool _allowClose;
    private readonly InlineFileRenameSession _renameSession = new();
    private bool _transientPopupOpen;
    private ContextMenu? _groupMenu;
    private DesktopItem[] _draggedItems = [];
    private DeskGroup? _draggedTab;
    private System.Windows.Point _tabDragOrigin;
    private Rect _tabSourceScreenBounds;
    private TabDragMode _tabDragMode;
    private DeskGroup? _tabMergeTarget;
    private GroupDragPreviewWindow? _groupPreview;
    private bool _isCompletingGroupDrag;
    private int _groupGestureVersion;
    private Rect _mergeTargetBounds;
    private bool _mergeDropTargetActive;
    private double? _dragShellOpacity;
    private bool _isApplyingCollapse;
    private DeskGroup? _mergeDragTarget;
    private HwndSource? _windowSource;

    public static readonly DependencyProperty IsItemDragActiveProperty = DependencyProperty.Register(
        nameof(IsItemDragActive), typeof(bool), typeof(DeskGroupWindow), new PropertyMetadata(false));

    public bool IsItemDragActive
    {
        get => (bool)GetValue(IsItemDragActiveProperty);
        private set => SetValue(IsItemDragActiveProperty, value);
    }

    public DeskGroup Group { get; private set; }
    public IReadOnlyList<DeskGroup> Members => _tabMembers;
    public nint DesktopHostHandle { get; private set; }
    internal bool HasGroupDragPreview => _groupPreview is not null;

    public DeskGroupWindow(
        DeskGroup group,
        DeskGroupWindowCallbacks callbacks,
        DesktopLayerHostService desktopLayerHostService,
        DesktopDragStateService desktopDragStateService)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _desktopLayerHostService = desktopLayerHostService ?? throw new ArgumentNullException(nameof(desktopLayerHostService));
        _desktopDragStateService = desktopDragStateService
                                   ?? throw new ArgumentNullException(nameof(desktopDragStateService));
        _desktopDragStateService.Changed += DesktopDragStateService_Changed;
        IsItemDragActive = _desktopDragStateService.IsActive;
        Closed += (_, _) =>
        {
            ResetGroupDragVisuals();
            _desktopDragStateService.Changed -= DesktopDragStateService_Changed;
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
        };
        InitializeComponent();
        _marqueeSelection = new MarqueeSelectionController(
            SelectionSurface, SelectionMarquee, () => Group.Items, FindItemTile);
        DataContext = Group;
        Left = Group.X;
        Top = Group.Y;
        Width = Math.Max(MinimumWidth, Group.Width);
        Height = Math.Max(MinimumHeight, Group.Height);
        ConfigureTabs([group], group);
        LocationChanged += (_, _) => SynchronizeBounds();
        SizeChanged += (_, _) => SynchronizeBounds();
    }

    public bool ContainsGroup(string groupId) =>
        _tabMembers.Any(group => group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));

    public void ConfigureTabs(IEnumerable<DeskGroup> groups, DeskGroup? activeGroup = null)
    {
        var ordered = groups.OrderBy(group => group.TabOrder).ThenBy(group => group.Name).ToArray();
        _tabMembers.Clear();
        foreach (var group in ordered) _tabMembers.Add(group);
        TabsHost.ItemsSource = _tabMembers;
        TabsBar.Visibility = _tabMembers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SwitchGroup(activeGroup is not null && _tabMembers.Contains(activeGroup) ? activeGroup : ordered[0]);
    }

    private void SwitchGroup(DeskGroup group)
    {
        if (!_tabMembers.Contains(group)) return;
        foreach (var item in _tabMembers.SelectMany(member => member.Items).Where(item => item.IsRenaming))
            _ = CommitRenameAsync(item, restoreFocus: false);
        foreach (var member in _tabMembers)
        {
            member.IsActive = ReferenceEquals(member, group);
            foreach (var item in member.Items) item.IsSelected = false;
            member.IsRenaming = false;
        }
        Group = group;
        DataContext = group;
        ApplyCollapsedState(animate: false);
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    public void EnsureDesktopPlacement()
        => EnsureDesktopPlacement(force: false);

    private void EnsureDesktopPlacement(bool force)
    {
        if (!IsLoaded || (!force && IsActive)) return;
        var handle = new WindowInteropHelper(this).Handle;
        var result = _desktopLayerHostService.TryPlaceAboveDesktopContent(handle);
        DesktopHostHandle = result.IsAttached ? result.HostHandle : 0;
    }

    public void RefreshDesktopPlacement()
    {
        EnsureDesktopPlacement();
    }

    public void BeginRename(DesktopItem item)
    {
        if (!Group.Items.Contains(item)) return;
        if (_renameSession.IsCommitting(item)) return;
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

    public void SetActivationEnabled(bool enabled)
    {
        if (!IsLoaded) return;
        var handle = new WindowInteropHelper(this).Handle;
        _ = _desktopLayerHostService.ConfigureAsDesktopToolWindow(handle, preventActivation: !enabled);
        if (enabled)
        {
            if (!IsActive) Activate();
            Dispatcher.BeginInvoke(() =>
            {
                if (!Group.IsRenaming && !Group.Items.Any(item => item.IsRenaming)) Keyboard.Focus(SelectionSurface);
            }, DispatcherPriority.Input);
        }
        else EnsureDesktopPlacement(force: true);
    }

    public void SetTransientPopupOpen(bool open) => _transientPopupOpen = open;

    public void SetMergeDropTarget(bool active)
    {
        if (_mergeDropTargetActive == active) return;
        _mergeDropTargetActive = active;
        var region = GetMergeDropRegion();
        var local = region.TranslatePoint(new System.Windows.Point(), GroupShell);
        MergeDropOverlay.Margin = new Thickness(local.X, local.Y,
            Math.Max(0, GroupShell.ActualWidth - local.X - region.ActualWidth),
            Math.Max(0, GroupShell.ActualHeight - local.Y - region.ActualHeight));
        AnimateDouble(MergeDropOverlay, OpacityProperty, active ? 1 : 0,
            GroupDragTransitionMilliseconds);
    }

    private FrameworkElement GetMergeDropRegion() => _tabMembers.Count > 1 ? TabsHost : GroupTitleArea;

    public Rect GetMergeDropScreenBounds()
    {
        var region = GetMergeDropRegion();
        if (!IsVisible || !region.IsVisible || region.ActualWidth <= 0 || region.ActualHeight <= 0) return Rect.Empty;
        return new Rect(region.PointToScreen(new System.Windows.Point()),
            region.PointToScreen(new System.Windows.Point(region.ActualWidth, region.ActualHeight)));
    }

    public void SetMergeSourceHover(Rect? targetBounds)
    {
        _mergeTargetBounds = targetBounds ?? Rect.Empty;
        if (_draggedTab is not null || !_isMovingWindow) return;
        if (targetBounds is not null) EnsureGroupPreview();
    }

    public void PlayMergeCompletedAnimation(System.Windows.Point sourcePosition)
    {
        PlayContentArrival(sourcePosition, merging: true);
    }

    public void PlayDetachedAnimation(System.Windows.Point sourcePosition)
    {
        PlayContentArrival(sourcePosition, merging: false);
    }

    internal bool IsMergeDropVisualActive =>
        MergeDropOverlay.Opacity > 0.8
        && MergeDropOverlay.Background is SolidColorBrush { Color.A: 0 }
        && MergeDropOverlay.BorderThickness.Left > 0
        && Math.Abs(ItemsScroller.Opacity - 1) < 0.001
        && Math.Abs(GroupTitleArea.Opacity - 1) < 0.001;

    internal async Task<bool> WaitForMergeDropVisualForSmokeTestAsync(bool active)
    {
        var deadline = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < deadline)
        {
            if (active ? IsMergeDropVisualActive && MergeDropOverlay.Opacity >= 0.98
                       : MergeDropOverlay.Opacity <= 0.02) return true;
            await Task.Delay(16);
        }
        return false;
    }

    internal static bool ValidateTabDropIntentForSmokeTest() =>
        TabDragPolicy.ResolveIntent(TabDragMode.Reorder, false, true) == TabDropIntent.Reorder
        && TabDragPolicy.ResolveIntent(TabDragMode.Transfer, true, false) == TabDropIntent.Reorder
        && TabDragPolicy.ResolveIntent(TabDragMode.Transfer, false, false) == TabDropIntent.Detach
        && TabDragPolicy.ResolveIntent(TabDragMode.Transfer, false, true) == TabDropIntent.Merge;

    internal bool ValidateMergeDropRegionForSmokeTest()
    {
        bool CheckRegion()
        {
            UpdateLayout();
            var bounds = GetMergeDropScreenBounds();
            var region = GetMergeDropRegion();
            var center = region.PointToScreen(new System.Windows.Point(region.ActualWidth / 2, region.ActualHeight / 2));
            var body = ItemsScroller.PointToScreen(new System.Windows.Point(ItemsScroller.ActualWidth / 2,
                ItemsScroller.ActualHeight / 2));
            var menu = GroupMenuButton.PointToScreen(new System.Windows.Point(GroupMenuButton.ActualWidth / 2,
                GroupMenuButton.ActualHeight / 2));
            return !bounds.IsEmpty && bounds.Contains(center) && !bounds.Contains(body) && !bounds.Contains(menu);
        }
        var members = _tabMembers.ToArray();
        var active = Group;
        try
        {
            var tabsWork = CheckRegion();
            ConfigureTabs([active], active);
            return tabsWork && CheckRegion();
        }
        finally { ConfigureTabs(members, active); }
    }

    internal async Task<bool> ValidateGroupSeparationAnimationAsync()
    {
        var bounds = new Rect(Left, Top, Width, Height);
        var persistedWidth = Group.Width;
        var persistedHeight = Group.Height;
        EnsureGroupPreview();
        var preview = _groupPreview!;
        var destination = bounds;
        destination.Offset(bounds.Width + 40, 18);
        preview.Follow(destination.TopLeft);
        var deadline = Environment.TickCount64 + 1500;
        while ((preview.PreviewBounds.TopLeft - destination.TopLeft).Length >= 1 && Environment.TickCount64 < deadline)
            await Task.Delay(16);
        var separated = (preview.PreviewBounds.TopLeft - destination.TopLeft).Length < 1
                        && preview.PreviewBounds.Size == bounds.Size;
        var completed = await preview.CompleteAsync(bounds, merge: true);
        ResetGroupDragVisuals();
        return separated && completed && _groupPreview is null
               && new Rect(Left, Top, Width, Height) == bounds
               && Math.Abs(Group.Width - persistedWidth) < 0.001
               && Math.Abs(Group.Height - persistedHeight) < 0.001;
    }

    internal bool ValidateHeaderDragPolicyForSmokeTest() =>
        ShouldStartHeaderDrag(GroupTitleArea, clickCount: 1)
        && !ShouldStartHeaderDrag(GroupTitleArea, clickCount: 2);

    internal async Task<bool> ValidateHeaderActivationForSmokeTestAsync(string? outputDirectory)
    {
        var bounds = new Rect(Left, Top, Width, Height);
        var competitor = new Window { Width = 1, Height = 1, Opacity = 0, ShowInTaskbar = false };
        try
        {
            competitor.Show();
            var targets = new List<UIElement> { GroupHeader, GroupTitleArea, GroupMenuButton };
            var tabContainer = TabsHost.ItemContainerGenerator.ContainerFromItem(Group);
            var tab = tabContainer is null ? null : FindVisualChild<Button>(tabContainer);
            if (tab is not null) targets.Add(tab);
            foreach (var target in targets)
            {
                competitor.Activate();
                SetActivationEnabled(false);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (IsActive) return false;
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var activated = IsActive;
                (ReferenceEquals(target, tab) ? target : GroupHeader).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
                if (!activated || _isMovingWindow || _draggedTab is not null
                    || new Rect(Left, Top, Width, Height) != bounds) return false;
            }
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(GroupShell.ActualWidth), (int)Math.Ceiling(GroupShell.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(GroupShell);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(outputDirectory, "header-activation.png"));
                encoder.Save(output);
            }
            return true;
        }
        finally { competitor.Close(); }
    }

    internal bool ValidateLiquidAppearanceMenuForSmokeTest()
    {
        MenuButton_Click(this, new RoutedEventArgs());
        var menu = _groupMenu;
        var customMenuWorks = menu is not null
                              && ReferenceEquals(menu.Style, FindResource("LiquidContextMenu"))
                              && menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "收纳盒颜色"))
                              && menu.Items.OfType<MenuItem>().Any(item => item.Header is Grid);
        if (menu is not null) menu.IsOpen = false;
        return customMenuWorks;
    }

    internal bool ValidateSimulatedLiquidGlassForSmokeTest() =>
        GroupShell.Effect is null
        && Math.Abs(GroupShell.CornerRadius.TopLeft - 18) < 0.1
        && Group.GlassBackground is SolidColorBrush { Color.A: <= 148 };

    internal bool ValidateInlineGroupRenameForSmokeTest()
    {
        BeginGroupRename();
        var works = Group.IsRenaming && Group.EditName == Group.Name;
        CancelGroupRename();
        return works && !Group.IsRenaming;
    }

    internal bool ValidateIconNameAlignmentForSmokeTest(DesktopItem singleLineItem, DesktopItem wrappedNameItem)
    {
        ItemsHost.UpdateLayout();
        var singleLineContainer = ItemsHost.ItemContainerGenerator.ContainerFromItem(singleLineItem);
        var wrappedNameContainer = ItemsHost.ItemContainerGenerator.ContainerFromItem(wrappedNameItem);
        if (singleLineContainer is null || wrappedNameContainer is null) return false;

        var singleLineIcon = FindVisualChildren<System.Windows.Controls.Image>(singleLineContainer).FirstOrDefault();
        var wrappedNameIcon = FindVisualChildren<System.Windows.Controls.Image>(wrappedNameContainer).FirstOrDefault();
        if (singleLineIcon is null || wrappedNameIcon is null) return false;

        var singleLineTop = singleLineIcon.TranslatePoint(new System.Windows.Point(), ItemsHost).Y;
        var wrappedNameTop = wrappedNameIcon.TranslatePoint(new System.Windows.Point(), ItemsHost).Y;
        return Math.Abs(singleLineTop - wrappedNameTop) < 0.1;
    }

    internal bool ValidateInlineRenameLayoutForSmokeTest(DesktopItem item)
    {
        item.EditName = "这是一个很长但不能撑开编辑区域的文件名称.txt";
        item.IsRenaming = true;
        ItemsHost.UpdateLayout();

        var tile = FindItemTile(item);
        var editor = tile is null ? null : FindVisualChild<TextBox>(tile);
        var icon = tile is null ? null : FindVisualChild<System.Windows.Controls.Image>(tile);
        var label = tile is null
            ? null
            : FindVisualChildren<TextBlock>(tile).FirstOrDefault(text => text.Text == item.Name);
        var works = editor is not null
                    && icon is not null
                    && label?.Visibility == Visibility.Collapsed
                    && editor.ActualWidth is > 0 and <= 72.5
                    && editor.TranslatePoint(new System.Windows.Point(), tile).Y
                       >= icon.TranslatePoint(new System.Windows.Point(), tile).Y + icon.ActualHeight - 0.5;
        item.IsRenaming = false;
        return works;
    }

    public void ClearTransientState(bool commitRename = true)
    {
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
        if (_mergeDragTarget is not null) _callbacks.MergeDragTargetChanged(Group, null);
        if (_tabMergeTarget is not null && _draggedTab is not null)
            _callbacks.MergeDragTargetChanged(_draggedTab, null);
        _mergeDragTarget = null;
        _isMovingWindow = false;
        _draggedTab = null;
        _tabDragMode = TabDragMode.None;
        _tabMergeTarget = null;
        ResetGroupDragVisuals();
        UpdateSelectionDisplay();
        SetActivationEnabled(false);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _ = _desktopLayerHostService.ConfigureAsDesktopToolWindow(handle, preventActivation: true);
        EnsureDesktopPlacement();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != MouseActivateMessage) return IntPtr.Zero;
        _ = _desktopLayerHostService.ConfigureAsDesktopToolWindow(hwnd, preventActivation: false);
        Dispatcher.BeginInvoke(() =>
        {
            if (!Group.IsRenaming && !Group.Items.Any(item => item.IsRenaming)) Keyboard.Focus(SelectionSurface);
        }, DispatcherPriority.Input);
        handled = true;
        return MouseActivate;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_transientPopupOpen && !IsActive) ClearTransientState();
        }, DispatcherPriority.Background);
    }

    private void SelectionSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || IsInsideTaggedElement(e.OriginalSource as DependencyObject, "DeskItemTile")) return;
        _callbacks.InteractionStarted(Group);
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

    private void SelectionSurface_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _marqueeSelection.Cancel();
        UpdateSelectionDisplay();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCompletingGroupDrag) { e.Handled = true; return; }
        _callbacks.InteractionStarted(Group);
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
        _isMovingWindow = true;
        _windowDragScreenStart = PointToScreen(e.GetPosition(this));
        _windowDragOrigin = new System.Windows.Point(Left, Top);
        SetActivationEnabled(true);
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
        var current = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _windowDragOrigin.X + (current.X - _windowDragScreenStart.X) / dpi.DpiScaleX;
        Top = _windowDragOrigin.Y + (current.Y - _windowDragScreenStart.Y) / dpi.DpiScaleY;
        var target = _callbacks.FindMergeTarget(Group, current);
        if (!ReferenceEquals(target, _mergeDragTarget))
        {
            _mergeDragTarget = target;
            _callbacks.MergeDragTargetChanged(Group, target);
        }
        _groupPreview?.Follow(new System.Windows.Point(Left, Top),
            _mergeTargetBounds.IsEmpty ? null : _mergeTargetBounds);
        e.Handled = true;
    }

    private async void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMovingWindow) return;
        _isMovingWindow = false;
        ((UIElement)sender).ReleaseMouseCapture();
        var target = _mergeDragTarget;
        _mergeDragTarget = null;
        if (target is not null)
        {
            e.Handled = true;
            _isCompletingGroupDrag = true;
            var version = ++_groupGestureVersion;
            var preview = _groupPreview;
            try
            {
                if (preview is not null && !_mergeTargetBounds.IsEmpty
                    && await preview.CompleteAsync(_mergeTargetBounds, merge: true)
                    && ReferenceEquals(preview, _groupPreview))
                    _callbacks.MergeGroups(Group, target);
            }
            finally
            {
                _callbacks.MergeDragTargetChanged(Group, null);
                if (version == _groupGestureVersion) ResetGroupDragVisuals();
            }
            return;
        }
        _callbacks.MergeDragTargetChanged(Group, null);
        await ReturnGroupPreviewAsync(new Rect(Left, Top, Width, Height));
        SynchronizeBounds();
        e.Handled = true;
    }

    private void Header_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isMovingWindow) return;
        _isMovingWindow = false;
        _mergeDragTarget = null;
        _callbacks.MergeDragTargetChanged(Group, null);
        _ = ReturnGroupPreviewAsync(new Rect(Left, Top, Width, Height));
        SynchronizeBounds();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Group.IsCollapsed) return;
        Width = Math.Max(MinimumWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinimumHeight, Height + e.VerticalChange);
        SynchronizeBounds();
    }

    private void SynchronizeBounds()
    {
        if (!IsLoaded || _isApplyingCollapse) return;
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
        var collapsed = !Group.IsCollapsed;
        foreach (var member in _tabMembers) member.IsCollapsed = collapsed;
        ApplyCollapsedState(animate: true);
        _callbacks.StateChanged(Group);
    }

    private void ApplyCollapsedState(bool animate)
    {
        var collapsed = Group.IsCollapsed;
        var targetHeight = collapsed
            ? HeaderHeight + (_tabMembers.Count > 1 ? TabsHeight : 0)
            : Math.Max(MinimumHeight, Group.Height);
        ResizeHandle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (!animate)
        {
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            ItemsScroller.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            ItemsScroller.Opacity = collapsed ? 0 : 1;
            return;
        }

        _isApplyingCollapse = true;
        if (!collapsed)
        {
            ItemsScroller.Visibility = Visibility.Visible;
            ItemsScroller.Opacity = 0;
            ItemsScroller.BeginAnimation(OpacityProperty,
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
                ItemsScroller.Visibility = Visibility.Collapsed;
                ItemsScroller.Opacity = 0;
            }
            _isApplyingCollapse = false;
            EnsureDesktopPlacement();
        };
        BeginAnimation(HeightProperty, animation);
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCompletingGroupDrag) { e.Handled = true; return; }
        if (sender is not FrameworkElement { DataContext: DeskGroup group } tab) return;
        _callbacks.InteractionStarted(group);
        SwitchGroup(group);
        _draggedTab = group;
        _tabDragOrigin = e.GetPosition(this);
        _tabSourceScreenBounds = GetMergeDropScreenBounds();
        _tabDragMode = TabDragMode.Pending;
        SetActivationEnabled(true);
        tab.CaptureMouse();
        e.Handled = true;
    }

    private void Tab_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggedTab is null || e.LeftButton != MouseButtonState.Pressed || _tabMembers.Count < 2) return;
        var current = e.GetPosition(TabsHost);
        _tabDragMode = TabDragPolicy.ResolveMode(_tabDragMode, e.GetPosition(this) - _tabDragOrigin);
        if (_tabDragMode == TabDragMode.Pending) return;
        if (_tabDragMode == TabDragMode.Transfer)
        {
            var screenPoint = PointToScreen(e.GetPosition(this));
            var pointerInsideSource = _tabSourceScreenBounds.Contains(screenPoint);
            var mergeTarget = pointerInsideSource ? null : _callbacks.FindMergeTarget(_draggedTab, screenPoint);
            var intent = TabDragPolicy.ResolveIntent(_tabDragMode, pointerInsideSource, mergeTarget is not null);
            UpdateTabDragPreview(_draggedTab, mergeTarget, intent, screenPoint);
            e.Handled = true;
            return;
        }

        var cellWidth = Math.Max(1, TabsHost.ActualWidth / _tabMembers.Count);
        var targetIndex = Math.Clamp((int)(current.X / cellWidth), 0, _tabMembers.Count - 1);
        var currentIndex = _tabMembers.IndexOf(_draggedTab);
        if (currentIndex == targetIndex) return;
        _tabMembers.Move(currentIndex, targetIndex);
        for (var index = 0; index < _tabMembers.Count; index++) _tabMembers[index].TabOrder = index;
        _callbacks.TabOrderChanged(_tabMembers.ToArray());
        e.Handled = true;
    }

    private async void Tab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedTab is null) return;
        var draggedTab = _draggedTab;
        var pointerInWindow = e.GetPosition(this);
        var screenPoint = PointToScreen(pointerInWindow);
        var pointerInsideSource = _tabSourceScreenBounds.Contains(screenPoint);
        var mergeTarget = _tabDragMode != TabDragMode.Transfer || pointerInsideSource
            ? null : _callbacks.FindMergeTarget(draggedTab, screenPoint);
        var intent = TabDragPolicy.ResolveIntent(_tabDragMode, pointerInsideSource, mergeTarget is not null);
        if (_tabDragMode == TabDragMode.Transfer)
            UpdateTabDragPreview(draggedTab, mergeTarget, intent, screenPoint);
        var preview = _groupPreview;
        _draggedTab = null;
        _tabDragMode = TabDragMode.None;
        if (sender is UIElement element && element.IsMouseCaptured) element.ReleaseMouseCapture();
        e.Handled = true;
        _isCompletingGroupDrag = true;
        var version = ++_groupGestureVersion;
        try
        {
            var destination = intent == TabDropIntent.Merge && !_mergeTargetBounds.IsEmpty
                ? _mergeTargetBounds : GetDetachedPreviewBounds(screenPoint);
            if (intent == TabDropIntent.Reorder)
                destination = new Rect(Left, Top, Width, Height);
            var completed = preview is null
                            || await preview.CompleteAsync(destination, merge: intent == TabDropIntent.Merge);
            if (!completed || (preview is not null && !ReferenceEquals(preview, _groupPreview))) return;
            if (intent == TabDropIntent.Merge && mergeTarget is not null)
                _callbacks.MergeTabIntoGroup(draggedTab, mergeTarget);
            else if (intent == TabDropIntent.Detach)
                _callbacks.DetachGroup(draggedTab, screenPoint);
        }
        finally
        {
            _callbacks.MergeDragTargetChanged(draggedTab, null);
            _tabMergeTarget = null;
            if (version == _groupGestureVersion) ResetGroupDragVisuals();
        }
    }

    private void Tab_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggedTab is not null) ClearTabDragPreview(_draggedTab);
        _draggedTab = null;
        _tabDragMode = TabDragMode.None;
    }

    private void UpdateTabDragPreview(DeskGroup source, DeskGroup? mergeTarget, TabDropIntent intent,
        System.Windows.Point screenPoint)
    {
        if (!ReferenceEquals(_tabMergeTarget, mergeTarget))
        {
            _tabMergeTarget = mergeTarget;
            _callbacks.MergeDragTargetChanged(source, mergeTarget);
        }
        if (intent != TabDropIntent.Reorder) EnsureGroupPreview();
        _groupPreview?.Follow(intent == TabDropIntent.Reorder
                ? new System.Windows.Point(Left, Top) : GetDetachedPreviewBounds(screenPoint).TopLeft,
            _mergeTargetBounds.IsEmpty ? null : _mergeTargetBounds, visible: intent != TabDropIntent.Reorder);
        if (_groupPreview is not null)
        {
            // 原盒保留轮廓作为抽离来源；移回时浮动卡片归位，不改变实际窗口尺寸。
            SetDragShellOpacity(1);
        }
    }

    private void ClearTabDragPreview(DeskGroup source)
    {
        _callbacks.MergeDragTargetChanged(source, null);
        _tabMergeTarget = null;
        _ = ReturnGroupPreviewAsync(new Rect(Left, Top, Width, Height));
    }

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item }) return;
        if (item.IsRenaming) return;
        _dragStart = e.GetPosition(this);
        var screenPoint = PointToScreen(_dragStart);
        _openGestureTracker.RegisterMouseDown(item.Path,
            new System.Drawing.Point((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)), e.Timestamp);
        _callbacks.InteractionStarted(Group);
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
            MessageBox.Show($"无法打开该项目。\n\n{error}", "MiniC",
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
        var paths = selected.Select(candidate => candidate.Path).ToArray();
        var data = new System.Windows.DataObject();
        data.SetData(InternalPathsFormat, paths);
        data.SetData(System.Windows.DataFormats.FileDrop, paths);
        System.Windows.DragDropEffects effect;
        _desktopDragStateService.SetActive(true);
        _callbacks.SetDesktopDropTargetActive(true);
        try
        {
            effect = DragPreviewWindow.Run(source, selected, data,
                System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move,
                GetDragPreviewOffsets(selected, item));
        }
        finally
        {
            ClearDropTargets();
            _desktopDragStateService.SetActive(false);
            _callbacks.SetDesktopDropTargetActive(false);
            _draggedItems = [];
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
        if (e.Key == Key.Escape && (_groupPreview is not null || _draggedTab is not null || _isMovingWindow))
        {
            var source = _draggedTab ?? Group;
            _draggedTab = null;
            _isMovingWindow = false;
            _tabDragMode = TabDragMode.None;
            _mergeDragTarget = null;
            _callbacks.MergeDragTargetChanged(source, null);
            if (Mouse.Captured is UIElement captured) captured.ReleaseMouseCapture();
            _ = ReturnGroupPreviewAsync(new Rect(Left, Top, Width, Height));
            e.Handled = true;
            return;
        }
        if (_isCompletingGroupDrag) { e.Handled = true; return; }
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
                    MessageBox.Show($"无法打开该项目。\n\n{error}", "MiniC",
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

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
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
        if (succeeded && restoreFocus && IsActive && Keyboard.FocusedElement is TextBox { DataContext: DesktopItem focused }
            && ReferenceEquals(focused, item)) Keyboard.Focus(SelectionSurface);
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
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
        if (!CollectionReorderService.MoveBlockToIndex(Group.Items, _draggedItems, targetIndex)) return true;
        Group.ItemOrder = Group.Items.Select(item => Path.GetFileName(item.Path)).ToList();
        _callbacks.StateChanged(Group);
        return true;
    }

    private int FindItemTargetIndex(System.Windows.Point point)
    {
        var entries = Group.Items.Select((item, index) =>
        {
            var tile = FindItemTile(item);
            var bounds = Rect.Empty;
            if (tile is null) return (Index: index, Bounds: bounds);
            try
            {
                bounds = tile.TransformToAncestor(SelectionSurface)
                    .TransformBounds(new Rect(new System.Windows.Point(), tile.RenderSize));
            }
            catch (InvalidOperationException)
            {
                // 布局刷新期间项目可能短暂脱离可视树，本轮忽略即可。
            }
            return (Index: index, Bounds: bounds);
        }).Where(entry => !entry.Bounds.IsEmpty).ToArray();
        if (entries.Length == 0) return 0;

        if (Group.ViewMode == "List")
        {
            return entries.MinBy(entry => Math.Abs(entry.Bounds.Top + entry.Bounds.Height / 2 - point.Y)).Index;
        }

        return entries.MinBy(entry =>
        {
            var dx = entry.Bounds.Left + entry.Bounds.Width / 2 - point.X;
            var dy = entry.Bounds.Top + entry.Bounds.Height / 2 - point.Y;
            return dx * dx + dy * dy;
        }).Index;
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
        _groupMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menuStyle = (Style)FindResource("LiquidContextMenu");
        var itemStyle = (Style)FindResource("LiquidMenuItem");
        var separatorStyle = (Style)FindResource("LiquidMenuSeparator");
        MenuItem CreateItem(object header) => new()
        {
            Header = header,
            Style = itemStyle,
            ItemContainerStyle = itemStyle
        };
        Separator CreateSeparator() => new() { Style = separatorStyle };

        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            DataContext = Group,
            Style = menuStyle,
            ItemContainerStyle = itemStyle
        };
        _groupMenu = menu;
        var rename = CreateItem("重命名收纳盒");
        rename.Click += (_, _) => BeginGroupRename();
        menu.Items.Add(rename);

        var iconView = CreateItem("图标模式");
        iconView.IsCheckable = true;
        iconView.IsChecked = Group.ViewMode != "List";
        var listView = CreateItem("列表预览");
        listView.IsCheckable = true;
        listView.IsChecked = Group.ViewMode == "List";
        iconView.Click += (_, _) => SetViewMode("Icons");
        listView.Click += (_, _) => SetViewMode("List");
        menu.Items.Add(iconView);
        menu.Items.Add(listView);
        menu.Items.Add(CreateSeparator());

        var colorMenu = CreateItem("收纳盒颜色");
        foreach (var (theme, name, color) in GetThemeChoices())
        {
            var colorItem = CreateItem(CreateColorHeader(name, color));
            colorItem.IsCheckable = true;
            colorItem.IsChecked = Group.Theme == theme;
            colorItem.Click += (_, _) =>
            {
                Group.Theme = theme;
                _callbacks.StateChanged(Group);
            };
            colorMenu.Items.Add(colorItem);
        }
        menu.Items.Add(colorMenu);
        menu.Items.Add(CreateOpacityItem(itemStyle));
        menu.Items.Add(CreateSeparator());

        var collapse = CreateItem(Group.IsCollapsed ? "展开收纳盒" : "折叠收纳盒");
        collapse.Click += (_, _) => ToggleCollapsed();
        menu.Items.Add(collapse);

        var merge = CreateItem("合并到");
        var mergeTargets = _callbacks.GetMergeTargets(Group);
        foreach (var target in mergeTargets)
        {
            var targetItem = CreateItem(target.Name);
            targetItem.Click += (_, _) => _callbacks.MergeGroups(Group, target);
            merge.Items.Add(targetItem);
        }
        merge.IsEnabled = merge.Items.Count > 0;
        menu.Items.Add(merge);
        if (_tabMembers.Count > 1)
        {
            var detach = CreateItem("移出标签组");
            detach.Click += (_, _) => _callbacks.DetachGroup(Group,
                PointToScreen(new System.Windows.Point(Width / 2, HeaderHeight)));
            menu.Items.Add(detach);
        }
        menu.Items.Add(CreateSeparator());

        var create = CreateItem("新建收纳盒");
        create.Click += (_, _) => _callbacks.CreateGroup(Group);
        menu.Items.Add(create);
        var dissolve = CreateItem("解散收纳盒");
        dissolve.Click += (_, _) => _callbacks.DissolveGroup(Group);
        menu.Items.Add(dissolve);
        menu.Closed += (_, _) => { if (ReferenceEquals(_groupMenu, menu)) _groupMenu = null; };
        menu.IsOpen = true;
    }

    private MenuItem CreateOpacityItem(Style itemStyle)
    {
        var valueText = new TextBlock
        {
            Text = $"{Group.Opacity:P0}",
            Foreground = Group.SecondaryForeground,
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var slider = new Slider
        {
            Minimum = 0.22,
            Maximum = 0.92,
            Value = Group.Opacity,
            TickFrequency = 0.02,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 7, 0, 0),
            Width = 190,
            Style = (Style)FindResource("LiquidSlider")
        };
        slider.ValueChanged += (_, args) =>
        {
            Group.Opacity = args.NewValue;
            valueText.Text = $"{args.NewValue:P0}";
            _callbacks.StateChanged(Group);
        };
        var panel = new Grid { Width = 206, Margin = new Thickness(0, 2, 0, 3) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = "透明度",
            Foreground = Group.PrimaryForeground,
            FontSize = 12
        });
        panel.Children.Add(valueText);
        Grid.SetRow(slider, 1);
        panel.Children.Add(slider);
        return new MenuItem
        {
            Header = panel,
            Style = itemStyle,
            StaysOpenOnClick = true,
            IsCheckable = false
        };
    }

    private static FrameworkElement CreateColorHeader(string name, string color)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 90, 105, 125)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 9, 0)
        });
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static (string Theme, string Name, string Color)[] GetThemeChoices() =>
    [
        ("White", "珍珠白", "#FFF7FAFF"),
        ("Blue", "冰川蓝", "#FFDDEBFF"),
        ("Mint", "薄荷绿", "#FFDCF7EE"),
        ("Purple", "淡雅紫", "#FFEAE0FF"),
        ("Amber", "暖阳橙", "#FFFFF0D2"),
        ("Graphite", "石墨黑", "#FF282C38")
    ];

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
        SetActivationEnabled(false);
    }

    private void CancelGroupRename()
    {
        Group.EditName = Group.Name;
        Group.IsRenaming = false;
        SetActivationEnabled(false);
    }

    private void UpdateSelectionDisplay() => SelectionDisplayService.Update(Group.Items);

    private void EnsureGroupPreview()
    {
        if (_groupPreview is not null) return;
        GroupShell.BeginAnimation(OpacityProperty, null);
        GroupShell.Opacity = 1;
        _groupPreview = new GroupDragPreviewWindow(GroupShell,
            new Rect(Left, Top, Width, Height), Group.GlassBackground,
            _draggedTab is not null ? TabsHeight : 0,
            _draggedTab is not null ? new System.Windows.Size(Group.Width, Group.Height) : null,
            ItemsScroller, GroupMaterial, GroupHeader);
        _groupPreview.Show();
        SetDragShellOpacity(_draggedTab is null ? 0 : 1);
    }

    private void SetDragShellOpacity(double opacity)
    {
        if (_dragShellOpacity == opacity) return;
        _dragShellOpacity = opacity;
        AnimateDouble(GroupShell, OpacityProperty, opacity, SystemParameters.ClientAreaAnimation ? 120 : 1);
    }

    private Rect GetDetachedPreviewBounds(System.Windows.Point screenPoint)
    {
        var local = PointFromScreen(screenPoint);
        var position = new System.Windows.Point(Left + local.X, Top + local.Y);
        return GroupDetachPlacement.GetBounds(position, new System.Windows.Size(Group.Width, Group.Height),
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
    }

    private async Task ReturnGroupPreviewAsync(Rect bounds)
    {
        var preview = _groupPreview;
        if (preview is null) return;
        _isCompletingGroupDrag = true;
        var version = ++_groupGestureVersion;
        try { await preview.CompleteAsync(bounds, merge: false); }
        finally
        {
            if (version == _groupGestureVersion && ReferenceEquals(preview, _groupPreview)) ResetGroupDragVisuals();
        }
    }

    private void ResetGroupDragVisuals()
    {
        _groupGestureVersion++;
        var preview = _groupPreview;
        _groupPreview = null;
        preview?.Close();
        _isCompletingGroupDrag = false;
        _mergeTargetBounds = Rect.Empty;
        _dragShellOpacity = null;
        GroupShell.BeginAnimation(OpacityProperty, null);
        GroupShell.Opacity = 1;
    }

    private void PlayContentArrival(System.Windows.Point sourcePosition, bool merging)
    {
        // 新宿主已经在最终位置，只有内部内容沿入场方向滑入，避免影响持久化坐标。
        var delta = sourcePosition - new System.Windows.Point(Left, Top);
        if (delta.Length < 1) delta = new Vector(0, -1);
        delta.Normalize();
        var transform = new TranslateTransform(delta.X * 24, delta.Y * 24);
        ItemsScroller.RenderTransform = transform;
        var duration = SystemParameters.ClientAreaAnimation ? GroupDragReleaseMilliseconds : 1;
        AnimateTransformDouble(transform, TranslateTransform.XProperty, 0, duration);
        AnimateTransformDouble(transform, TranslateTransform.YProperty, 0, duration);
        if (merging)
        {
            var tabOffset = new TranslateTransform(0, -10);
            TabsBar.RenderTransform = tabOffset;
            AnimateTransformDouble(tabOffset, TranslateTransform.YProperty, 0, duration);
        }
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
