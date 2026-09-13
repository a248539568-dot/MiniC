using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniC.Controls;
using MiniC.Services;
using MiniC.ViewModels;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace MiniC.Views;

/// <summary>只绘制未收纳项目的桌面图标层。</summary>
public partial class DesktopSurfaceWindow : Window
{
    private const int MouseActivateMessage = 0x0021;
    private static readonly IntPtr MouseActivate = new(1);
    private readonly ObservableCollection<DesktopItem> _items;
    private readonly DesktopSurfaceWindowCallbacks _callbacks;
    private readonly DesktopLayerHostService _desktopLayerHostService;
    private readonly DesktopDragStateService _desktopDragStateService;
    private readonly MarqueeSelectionController _marqueeSelection;
    private readonly DesktopItemOpenGestureTracker _openGestureTracker = new();
    private System.Windows.Point _dragStart;
    private bool _allowClose;
    private bool _dropTargetActive;
    private bool _renameCommitRunning;
    private bool _transientPopupOpen;
    private bool _showingBackgroundMenu;
    private HwndSource? _windowSource;

    public static readonly DependencyProperty IsItemDragActiveProperty = DependencyProperty.Register(
        nameof(IsItemDragActive), typeof(bool), typeof(DesktopSurfaceWindow), new PropertyMetadata(false));

    public bool IsItemDragActive
    {
        get => (bool)GetValue(IsItemDragActiveProperty);
        private set => SetValue(IsItemDragActiveProperty, value);
    }

    public DesktopSurfaceWindow(
        ObservableCollection<DesktopItem> items,
        DesktopSurfaceWindowCallbacks callbacks,
        DesktopLayerHostService desktopLayerHostService,
        DesktopDragStateService desktopDragStateService)
    {
        _items = items;
        _callbacks = callbacks;
        _desktopLayerHostService = desktopLayerHostService;
        _desktopDragStateService = desktopDragStateService;
        _desktopDragStateService.Changed += DesktopDragStateService_Changed;
        IsItemDragActive = _desktopDragStateService.IsActive;
        Closed += (_, _) =>
        {
            _desktopDragStateService.Changed -= DesktopDragStateService_Changed;
            _windowSource?.RemoveHook(WindowMessageHook);
            _windowSource = null;
        };
        InitializeComponent();
        _marqueeSelection = new MarqueeSelectionController(
            SelectionSurface, SelectionMarquee, () => _items, FindItemTile);
        DataContext = _items;
        UpdateDisplayBounds();
    }

    public void UpdateDisplayBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    public void EnsureDesktopPlacement()
    {
        if (!IsLoaded) return;
        var handle = new WindowInteropHelper(this).Handle;
        _ = _desktopLayerHostService.TryPlaceClosestToDesktopContent(handle, preventActivation: !IsActive);
    }

    public void RefreshDesktopPlacement()
    {
        EnsureDesktopPlacement();
    }

    public void SetDropTargetActive(bool active)
    {
        if (_dropTargetActive == active || !IsLoaded) return;
        _dropTargetActive = active;
        var handle = new WindowInteropHelper(this).Handle;
        if (active)
        {
            // AllowsTransparency 窗口的全透明像素会让 OLE Drop 穿透到 Explorer。
            // 拖动期间使用肉眼不可见的非零 Alpha，确保桌面空白处由本窗口命中。
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
            _ = _desktopLayerHostService.ApplyInteractiveRegions(handle, null);
            UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            _ = DwmFlush();
        }
        else
        {
            Background = System.Windows.Media.Brushes.Transparent;
            _ = _desktopLayerHostService.ApplyInteractiveRegions(handle, null);
        }
    }

    public void SetScreenPosition(DesktopItem item, int screenX, int screenY)
    {
        var local = PointFromScreen(new System.Windows.Point(screenX, screenY));
        item.X = local.X;
        item.Y = local.Y;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(screenX, screenY)).WorkingArea;
        var bottom = PointFromScreen(new System.Windows.Point(screenX, area.Bottom));
        item.AvailableHeight = Math.Max(DesktopIconPlacementService.TileHeight, bottom.Y - local.Y);
    }

    public void BeginRename(DesktopItem item)
    {
        if (!_items.Contains(item) || item.IsVirtual) return;
        _openGestureTracker.Reset();
        SetActivationEnabled(true);
        foreach (var candidate in _items) candidate.IsRenaming = false;
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

    internal bool ValidateInlineRenameLayoutForSmokeTest(DesktopItem item)
    {
        if (!_items.Contains(item)) return false;
        item.EditName = "这是一个很长但不能撑开编辑区域的文件名称.txt";
        item.IsRenaming = true;
        ItemsHost.Measure(new System.Windows.Size(200, 160));
        ItemsHost.Arrange(new Rect(0, 0, 200, 160));
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

    internal async Task<bool> ValidateRenameActivationForSmokeTestAsync(DesktopItem item)
    {
        // 独立离屏窗口：验证实际激活路径，不调用真实文件重命名或修改用户布局。
        Left = -30000;
        Top = -30000;
        Width = 200;
        Height = 160;
        Show();
        var descriptor = DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(Window));
        var layerWasHidden = false;
        EventHandler onOpacityChanged = (_, _) => layerWasHidden |= Opacity < 1;
        descriptor.AddValueChanged(this, onOpacityChanged);
        try
        {
            BeginRename(item);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var tile = FindItemTile(item);
            var editor = tile is null ? null : FindVisualChild<TextBox>(tile);
            var editingWorks = item.IsRenaming && editor is { Visibility: Visibility.Visible };
            CancelRename(item);
            return editingWorks && !layerWasHidden && Opacity == 1;
        }
        finally
        {
            descriptor.RemoveValueChanged(this, onOpacityChanged);
        }
    }

    public void SetActivationEnabled(bool enabled)
    {
        if (!IsLoaded) return;
        var handle = new WindowInteropHelper(this).Handle;
        _ = _desktopLayerHostService.ConfigureAsDesktopToolWindow(handle, preventActivation: !enabled);
        if (enabled)
        {
            if (!IsActive) Activate();
            _ = _desktopLayerHostService.TryPlaceClosestToDesktopContent(handle, preventActivation: false);
            Dispatcher.BeginInvoke(() =>
            {
                if (!_items.Any(item => item.IsRenaming)) Keyboard.Focus(SelectionSurface);
            }, DispatcherPriority.Input);
        }
        else EnsureDesktopPlacement();
    }

    public void SetTransientPopupOpen(bool open) => _transientPopupOpen = open;

    public void ClearTransientState(bool cancelRename = true)
    {
        _openGestureTracker.Reset();
        _marqueeSelection.Cancel();
        foreach (var item in _items)
        {
            item.IsSelected = false;
            item.IsDropTarget = false;
            if (cancelRename) item.IsRenaming = false;
        }
        SelectionDisplayService.Update(_items);
        if (_dropTargetActive) SetDropTargetActive(false);
        SetActivationEnabled(false);
    }

    public bool IsWindowAt(int screenX, int screenY)
    {
        if (!IsLoaded) return false;
        var handle = new WindowInteropHelper(this).Handle;
        var hit = WindowFromPoint(new NativePoint(screenX, screenY));
        return hit == handle || IsChild(handle, hit);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _ = _desktopLayerHostService.ConfigureAsDesktopToolWindow(handle, preventActivation: true);
        EnsureDesktopPlacement();
        _ = _desktopLayerHostService.ApplyInteractiveRegions(handle, null);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != MouseActivateMessage) return IntPtr.Zero;
        _ = _desktopLayerHostService.ConfigureAsDesktopToolWindow(hwnd, preventActivation: false);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_items.Any(item => item.IsRenaming)) Keyboard.Focus(SelectionSurface);
        }, DispatcherPriority.Input);
        handled = true;
        return MouseActivate;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_transientPopupOpen && !IsActive) ClearTransientState();
        }, DispatcherPriority.Background);
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        RestoreActiveDesktopPlacement();
        Dispatcher.BeginInvoke(RestoreActiveDesktopPlacement, DispatcherPriority.ContextIdle);
    }

    private void RestoreActiveDesktopPlacement()
    {
        if (!IsLoaded || !IsActive) return;
        var handle = new WindowInteropHelper(this).Handle;
        _ = _desktopLayerHostService.TryPlaceClosestToDesktopContent(handle, preventActivation: false);
    }

    private void SelectionSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || IsInsideTaggedElement(e.OriginalSource as DependencyObject, "DesktopItemTile")) return;
        _callbacks.InteractionStarted();
        SetActivationEnabled(true);
        _openGestureTracker.Reset();
        _marqueeSelection.Begin(e.GetPosition(SelectionSurface),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        SelectionDisplayService.Update(_items);
        e.Handled = true;
    }

    private void SelectionSurface_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_marqueeSelection.IsActive) return;
        if (e.LeftButton == MouseButtonState.Pressed)
            _marqueeSelection.Update(e.GetPosition(SelectionSurface));
        else
            _marqueeSelection.Complete(e.GetPosition(SelectionSurface));
        SelectionDisplayService.Update(_items);
        e.Handled = true;
    }

    private void SelectionSurface_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeSelection.IsActive) return;
        _marqueeSelection.Complete(e.GetPosition(SelectionSurface));
        SelectionDisplayService.Update(_items);
        e.Handled = true;
    }

    private async void SelectionSurface_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideTaggedElement(e.OriginalSource as DependencyObject, "DesktopItemTile")) return;
        _callbacks.InteractionStarted();
        e.Handled = true;
        await ShowBackgroundMenuAsync(e.GetPosition(this));
    }

    private void SelectionSurface_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _marqueeSelection.Cancel();
        SelectionDisplayService.Update(_items);
    }

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item }) return;
        if (item.IsRenaming) return;
        _dragStart = e.GetPosition(this);
        var screenPoint = PointToScreen(_dragStart);
        _openGestureTracker.RegisterMouseDown(item.Path,
            new System.Drawing.Point((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)), e.Timestamp);
        _callbacks.InteractionStarted();
        SetActivationEnabled(true);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) item.IsSelected = !item.IsSelected;
        else if (!item.IsSelected)
        {
            foreach (var candidate in _items) candidate.IsSelected = ReferenceEquals(candidate, item);
        }
        SelectionDisplayService.Update(_items);
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
            ? _items.Where(candidate => candidate.IsSelected)
                .OrderBy(candidate => ReferenceEquals(candidate, item) ? 0 : 1).ToArray()
            : [item];
        var paths = selected.Select(candidate => candidate.Path).ToArray();
        var filePaths = selected.Where(candidate => !candidate.IsVirtual)
            .Select(candidate => candidate.Path).ToArray();
        var data = new System.Windows.DataObject();
        data.SetData(DeskGroupWindow.InternalPathsFormat, paths);
        if (filePaths.Length > 0) data.SetData(System.Windows.DataFormats.FileDrop, filePaths);
        System.Windows.DragDropEffects effect;
        _desktopDragStateService.SetActive(true);
        SetDropTargetActive(true);
        try
        {
            effect = DragPreviewWindow.Run(source, selected, data,
                filePaths.Length > 0
                    ? System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move
                    : System.Windows.DragDropEffects.Move,
                GetDragPreviewOffsets(selected, item));
        }
        finally
        {
            ClearDropTargets();
            _desktopDragStateService.SetActive(false);
            SetDropTargetActive(false);
        }
        var cursor = System.Windows.Forms.Cursor.Position;
        await _callbacks.DragCompletedAsync(paths,
            new System.Windows.Point(cursor.X, cursor.Y), effect);
        e.Handled = true;
    }

    private async void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DesktopItem item }) return;
        _callbacks.InteractionStarted();
        if (!item.IsSelected)
        {
            foreach (var candidate in _items) candidate.IsSelected = ReferenceEquals(candidate, item);
        }
        SelectionDisplayService.Update(_items);
        var selected = _items.Where(candidate => candidate.IsSelected).ToArray();
        var point = PointToScreen(e.GetPosition(this));
        await _callbacks.ShowItemMenuAsync(selected, item,
            new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y)));
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.V)
        {
            _ = ShellContextMenuService.PasteToDesktop(System.Windows.Forms.Cursor.Position);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.A)
        {
            foreach (var item in _items) item.IsSelected = true;
            SelectionDisplayService.Update(_items);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            var selectedForDelete = _items.Where(item => item.IsSelected && !item.IsVirtual).ToArray();
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
            var selectedForOpen = _items.Where(item => item.IsSelected && !item.IsVirtual).ToArray();
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
            var clipboardSelected = _items.Where(item => item.IsSelected && !item.IsVirtual).ToArray();
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
        var selectedForRename = _items.Where(item => item.IsSelected && !item.IsVirtual).ToArray();
        if (selectedForRename.Length != 1) return;
        BeginRename(selectedForRename[0]);
        e.Handled = true;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        var item = _items.FirstOrDefault(candidate => candidate.IsRenaming);
        if (item is not null) CancelRename(item);
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

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_renameCommitRunning && sender is TextBox { DataContext: DesktopItem item } && item.IsRenaming)
            CancelRename(item);
    }

    private void CancelRename(DesktopItem item)
    {
        item.IsRenaming = false;
        item.EditName = DesktopFileService.GetRenameEditName(item);
        Keyboard.Focus(SelectionSurface);
    }

    private async Task CommitRenameAsync(DesktopItem item)
    {
        if (_renameCommitRunning || !item.IsRenaming) return;
        _renameCommitRunning = true;
        try
        {
            if (await _callbacks.RenameItemAsync(item, item.EditName))
            {
                item.IsRenaming = false;
                Keyboard.Focus(SelectionSurface);
            }
        }
        finally
        {
            _renameCommitRunning = false;
        }
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        ClearDropTargets();
        if (!TryGetDroppedPaths(e.Data, out var paths))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = e.Data.GetDataPresent(DeskGroupWindow.InternalPathsFormat)
            ? System.Windows.DragDropEffects.Move
            : ResolveDropEffect(e, paths);
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!TryGetDroppedPaths(e.Data, out var paths)) return;
        var effect = e.Data.GetDataPresent(DeskGroupWindow.InternalPathsFormat)
            ? System.Windows.DragDropEffects.Move
            : ResolveDropEffect(e, paths);
        e.Effects = effect;
        e.Handled = true;
        if (!e.Data.GetDataPresent(DeskGroupWindow.InternalPathsFormat))
        {
            var point = PointToScreen(e.GetPosition(this));
            await _callbacks.ExternalDropAsync(paths, point, effect);
        }
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
                                 && !path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase));
    }

    private void ClearDropTargets(DesktopItem? except = null)
    {
        foreach (var item in _items.Where(item => !ReferenceEquals(item, except))) item.IsDropTarget = false;
    }

    private void DesktopDragStateService_Changed(bool active) => IsItemDragActive = active;

    private static System.Windows.DragDropEffects ResolveDropEffect(
        System.Windows.DragEventArgs e,
        IReadOnlyCollection<string> paths)
    {
        if (e.Data.GetDataPresent(DeskGroupWindow.InternalPathsFormat))
            return System.Windows.DragDropEffects.Move;
        if (e.KeyStates.HasFlag(System.Windows.DragDropKeyStates.ControlKey)
            && e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Copy))
            return System.Windows.DragDropEffects.Copy;
        if (e.KeyStates.HasFlag(System.Windows.DragDropKeyStates.ShiftKey)
            && e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Move))
            return System.Windows.DragDropEffects.Move;
        var desktopRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var sameVolume = paths.All(path => string.Equals(
            Path.GetPathRoot(Path.GetFullPath(path)), desktopRoot, StringComparison.OrdinalIgnoreCase));
        if (sameVolume && e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Move))
            return System.Windows.DragDropEffects.Move;
        if (e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Copy))
            return System.Windows.DragDropEffects.Copy;
        return e.AllowedEffects.HasFlag(System.Windows.DragDropEffects.Move)
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
    }

    private static bool TryGetDroppedPaths(System.Windows.IDataObject data, out string[] paths)
    {
        paths = data.GetData(DeskGroupWindow.InternalPathsFormat, false) as string[]
                ?? data.GetData(System.Windows.DataFormats.FileDrop, true) as string[]
                ?? [];
        paths = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length > 0;
    }

    private FrameworkElement? FindItemTile(DesktopItem item) =>
        FindVisualChildren<FrameworkElement>(ItemsHost)
            .FirstOrDefault(element => Equals(element.Tag, "DesktopItemTile")
                                       && ReferenceEquals(element.DataContext, item));

    private async Task ShowBackgroundMenuAsync(System.Windows.Point localPoint)
    {
        if (_showingBackgroundMenu || !IsLoaded) return;
        _showingBackgroundMenu = true;
        try
        {
            var screenPoint = PointToScreen(localPoint);
            await _callbacks.ShowBackgroundMenuAsync(new System.Drawing.Point(
                (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
        }
        finally
        {
            _showingBackgroundMenu = false;
        }
    }

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

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject =>
        FindVisualChildren<T>(parent).FirstOrDefault();

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
