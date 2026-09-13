using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MiniC.Controllers;
using MiniC.Controls;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;

namespace MiniC;

/// <summary>
/// 应用程序入口，负责单实例互斥、第二实例唤醒以及应用生命周期清理。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = DesktopFallbackWatchdog.SingleInstanceMutexName;
    // 与旧版本共用事件名，保证升级后的第二次启动仍能唤起已运行实例。
    private const string ShowPanelEventName = "Local\\DeskNest.ShowPanel.4E31D7B6";
    private DesktopCoordinator? _coordinator;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showPanelEvent;
    private volatile bool _isExiting;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MiniCLogger.Error(nameof(App), e.Exception, "Unhandled WPF dispatcher exception.");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            MiniCLogger.Error(nameof(App), exception, "Unhandled application-domain exception.");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        MiniCLogger.Error(nameof(App), e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (DesktopFallbackWatchdog.TryParse(e.Args, out var watchedProcessId, out var watchedStartTime))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var restored = await DesktopFallbackWatchdog.WaitForExitAndRestoreAsync(
                watchedProcessId, watchedStartTime);
            Shutdown(restored ? 0 : 18);
            return;
        }

        // 验证多选拖动预览不折叠，并在桌面落位时保持原有网格相对位置。
        if (e.Args.Any(argument => argument.Equals("--multi-drag-layout-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            var first = new DesktopItem { Path = "first", Name = "first", X = 16, Y = 16 };
            var second = new DesktopItem { Path = "second", Name = "second", X = 98, Y = 104 };
            var offsets = new Dictionary<DesktopItem, System.Windows.Point>
            {
                [first] = new(0, 0),
                [second] = new(DesktopIconPlacementService.TileWidth, DesktopIconPlacementService.TileHeight)
            };
            var previewPreservesLayout = DragPreviewWindow.ValidateRelativeLayoutForSmokeTest(
                [first, second], offsets);
            var positions = new Dictionary<string, MiniC.Models.DesktopIconPositionState>
            {
                [first.Path] = new() { X = 16, Y = 16 },
                [second.Path] = new() { X = 98, Y = 104 }
            };
            new DesktopIconPlacementService().PlaceAt(
                [first.Path, second.Path], new System.Windows.Point(520, 420),
                [first, second], positions);
            var placementPreservesLayout =
                positions[second.Path].X - positions[first.Path].X == DesktopIconPlacementService.TileWidth
                && positions[second.Path].Y - positions[first.Path].Y == DesktopIconPlacementService.TileHeight;
            Shutdown(previewPreservesLayout && placementPreservesLayout ? 0 : 16);
            return;
        }

        // 验证重命名文本及最终后缀严格跟随 Explorer 的扩展名显示设置。
        if (e.Args.Any(argument => argument.Equals("--rename-policy-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var item = new DesktopItem { Path = @"C:\Temp\report.txt", Name = "report", IsDirectory = false };
            var hiddenEditName = DesktopFileService.GetRenameEditName(item, extensionsVisible: false);
            var visibleEditName = DesktopFileService.GetRenameEditName(item, extensionsVisible: true);
            var hiddenTarget = DesktopFileService.GetRenameTargetFileName(
                item.Path, "renamed", isDirectory: false, extensionsVisible: false);
            var visibleTarget = DesktopFileService.GetRenameTargetFileName(
                item.Path, "renamed.csv", isDirectory: false, extensionsVisible: true);
            var hiddenSelectionLength = DesktopFileService.GetRenameSelectionLength(item, hiddenEditName);
            var visibleSelectionLength = DesktopFileService.GetRenameSelectionLength(item, visibleEditName);
            var directory = new DesktopItem
                { Path = @"C:\Temp\folder", Name = "folder", IsDirectory = true };
            var directorySelectionLength = DesktopFileService.GetRenameSelectionLength(directory, directory.Name);
            var renameItems = new System.Collections.ObjectModel.ObservableCollection<DesktopItem> { item };
            var renameDragState = new DesktopDragStateService();
            var renameSurface = new DesktopSurfaceWindow(
                renameItems,
                new DesktopSurfaceWindowCallbacks(
                    (_, _, _) => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask,
                    (_, _) => Task.FromResult(true),
                    (_, _, _) => Task.CompletedTask,
                    () => { },
                    _ => Task.CompletedTask),
                new DesktopLayerHostService(),
                renameDragState);
            var inlineRenameLayoutWorks = renameSurface.ValidateInlineRenameLayoutForSmokeTest(item);
            var renameActivationWorks = await renameSurface.ValidateRenameActivationForSmokeTestAsync(item);
            renameSurface.ClosePermanently();
            Shutdown(hiddenEditName == "report"
                     && visibleEditName == "report.txt"
                     && hiddenTarget == "renamed.txt"
                     && visibleTarget == "renamed.csv"
                     && hiddenSelectionLength == "report".Length
                     && visibleSelectionLength == "report".Length
                     && directorySelectionLength == directory.Name.Length
                     && inlineRenameLayoutWorks && renameActivationWorks ? 0 : 15);
            return;
        }

        // 验证文件夹投放遵循移动/复制语义，移动不会重复生成“(2)”文件。
        if (e.Args.Any(argument => argument.Equals("--file-transfer-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            var root = Path.Combine(Path.GetTempPath(), $"MiniC-Smoke-{Guid.NewGuid():N}");
            var sourceDirectory = Path.Combine(root, "source");
            var targetDirectory = Path.Combine(root, "target");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);
            try
            {
                var service = new DesktopFileService();
                var moveSource = Path.Combine(sourceDirectory, "move.txt");
                await File.WriteAllTextAsync(moveSource, "move");
                var moved = await service.DropIntoFolderAsync([moveSource], targetDirectory, copy: false);
                var movedPath = Path.Combine(targetDirectory, "move.txt");
                var sameFolder = await service.DropIntoFolderAsync([movedPath], targetDirectory, copy: false);
                var copySource = Path.Combine(sourceDirectory, "copy.txt");
                await File.WriteAllTextAsync(copySource, "copy");
                var copied = await service.DropIntoFolderAsync([copySource], targetDirectory, copy: true);
                var valid = moved.Errors.Count == 0
                            && sameFolder.Errors.Count == 0
                            && copied.Errors.Count == 0
                            && File.Exists(movedPath)
                            && !File.Exists(Path.Combine(targetDirectory, "move (2).txt"))
                            && File.Exists(copySource)
                            && File.Exists(Path.Combine(targetDirectory, "copy.txt"));
                service.Dispose();
                Shutdown(valid ? 0 : 17);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            return;
        }

        // 验证盒内多选排序、标签顺序、活动标签和折叠窗口状态。
        if (e.Args.Any(argument => argument.Equals("--group-organization-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var firstItem = new DesktopItem { Path = "first", Name = "first" };
            var secondItem = new DesktopItem { Path = "second", Name = "second" };
            var thirdItem = new DesktopItem { Path = "third", Name = "third" };
            var fourthItem = new DesktopItem { Path = "fourth", Name = "fourth" };
            var items = new System.Collections.ObjectModel.ObservableCollection<DesktopItem>
                { firstItem, secondItem, thirdItem, fourthItem };
            var movedToEnd = CollectionReorderService.MoveBlock(items, [secondItem, thirdItem], 4)
                             && items.SequenceEqual([firstItem, fourthItem, secondItem, thirdItem]);
            var movedToStart = CollectionReorderService.MoveBlock(items, [secondItem, thirdItem], 0)
                                && items.SequenceEqual([secondItem, thirdItem, firstItem, fourthItem]);
            var movedToPointerSlot = CollectionReorderService.MoveBlockToIndex(items, [firstItem], 0)
                                     && items.SequenceEqual([firstItem, secondItem, thirdItem, fourthItem]);
            firstItem.IsSelected = true;
            SelectionDisplayService.Update(items);
            var singleSelectionShowsFullName = firstItem.ShowFullName
                                               && items.Where(item => !ReferenceEquals(item, firstItem))
                                                   .All(item => !item.ShowFullName);
            secondItem.IsSelected = true;
            SelectionDisplayService.Update(items);
            var multipleSelectionHidesFullName = items.All(item => !item.ShowFullName);

            var firstGroup = new DeskGroup { Name = "first", TabOrder = 2, Height = 280 };
            var secondGroup = new DeskGroup { Name = "second", TabOrder = 0, Height = 280 };
            var thirdGroup = new DeskGroup { Name = "third", TabOrder = 1, Height = 280 };
            firstItem.Name = "单行名称";
            secondItem.Name = "这是一个需要显示两行的桌面项目名称";
            secondGroup.Items.Add(firstItem);
            secondGroup.Items.Add(secondItem);
            var stateChangeCount = 0;
            var callbacks = new DeskGroupWindowCallbacks(
                _ => stateChangeCount++,
                (_, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.FromResult(true),
                (_, _, _, _) => Task.CompletedTask,
                _ => { },
                _ => { },
                _ => { },
                _ => { },
                _ => [],
                (_, _) => null,
                (_, _) => { },
                (_, _) => { },
                (_, _) => { },
                (_, _) => { },
                _ => { });
            var dragState = new DesktopDragStateService();
            var window = new DeskGroupWindow(
                firstGroup, callbacks, new DesktopLayerHostService(), dragState)
            {
                Opacity = 0,
                ShowActivated = false
            };
            window.ConfigureTabs([firstGroup, secondGroup, thirdGroup], secondGroup);
            var tabsRestored = window.Group == secondGroup
                               && window.Members.SequenceEqual([secondGroup, thirdGroup, firstGroup]);
            foreach (var group in window.Members) group.IsCollapsed = true;
            window.ConfigureTabs(window.Members.ToArray(), secondGroup);
            var collapsedHeightWorks = Math.Abs(window.Height - 76) < 0.5;
            foreach (var group in window.Members) group.IsCollapsed = false;
            window.ConfigureTabs(window.Members.ToArray(), secondGroup);
            var expandedHeightWorks = Math.Abs(window.Height - 280) < 0.5;
            window.SetViewMode("List", animate: false);
            var listPreviewWorks = secondGroup.ViewMode == "List";
            window.SetViewMode("Icons", animate: false);
            var iconModeRestored = secondGroup.ViewMode == "Icons" && stateChangeCount == 2;
            window.Show();
            var iconNameAlignmentWorks = window.ValidateIconNameAlignmentForSmokeTest(firstItem, secondItem);
            var inlineRenameLayoutWorks = window.ValidateInlineRenameLayoutForSmokeTest(firstItem);
            var groupSeparationAnimationWorks = await window.ValidateGroupSeparationAnimationAsync();
            firstGroup.Theme = "Graphite";
            secondGroup.Theme = "Blue";
            secondGroup.Opacity = 0.44;
            var independentAppearanceWorks = firstGroup.Theme == "Graphite"
                                             && Math.Abs(firstGroup.Opacity - 0.62) < 0.001
                                             && secondGroup.Theme == "Blue"
                                             && Math.Abs(secondGroup.Opacity - 0.44) < 0.001;
            var customAppearanceMenuWorks = window.ValidateLiquidAppearanceMenuForSmokeTest();
            var simulatedLiquidGlassWorks = window.ValidateSimulatedLiquidGlassForSmokeTest();
            var inlineGroupRenameWorks = window.ValidateInlineGroupRenameForSmokeTest();
            var tabDropIntentWorks = DeskGroupWindow.ValidateTabDropIntentForSmokeTest();
            var mergeDropRegionWorks = window.ValidateMergeDropRegionForSmokeTest();
            var tabMergeAppendWorks = DesktopCoordinator.ValidateTabMergeAppendForSmokeTest();
            var topologyGuardWorks = DesktopCoordinator.ValidateGroupTopologyGuardForSmokeTest();
            var headerDragPolicyWorks = window.ValidateHeaderDragPolicyForSmokeTest();
            var emptyMergeTargetWorks = DesktopCoordinator.ValidateEmptyMergeTargetForSmokeTest();
            dragState.SetActive(true);
            var sharedDragStateWorks = window.IsItemDragActive;
            dragState.SetActive(false);
            sharedDragStateWorks &= !window.IsItemDragActive;
            window.SetMergeDropTarget(true);
            await Task.Delay(210);
            var mergeEnterAnimationWorks = window.IsMergeDropVisualActive;
            window.SetMergeDropTarget(false);
            await Task.Delay(210);
            var mergeLeaveAnimationWorks = !window.IsMergeDropVisualActive;
            window.ClosePermanently();
            Shutdown(movedToEnd && movedToStart && movedToPointerSlot
                     && singleSelectionShowsFullName && multipleSelectionHidesFullName && tabsRestored
                     && collapsedHeightWorks && expandedHeightWorks
                     && listPreviewWorks && iconModeRestored
                     && independentAppearanceWorks && customAppearanceMenuWorks
                     && simulatedLiquidGlassWorks && inlineGroupRenameWorks && tabDropIntentWorks && mergeDropRegionWorks
                       && iconNameAlignmentWorks
                       && inlineRenameLayoutWorks
                       && groupSeparationAnimationWorks
                     && tabMergeAppendWorks
                     && topologyGuardWorks
                     && sharedDragStateWorks && headerDragPolicyWorks && emptyMergeTargetWorks
                     && mergeEnterAnimationWorks && mergeLeaveAnimationWorks ? 0 : 14);
            return;
        }

        // 验证桌面与收纳盒共用的框选控制器支持普通选择和 Ctrl 反选。
        if (e.Args.Any(argument => argument.Equals("--marquee-selection-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var first = new DesktopItem { Path = "first", Name = "first" };
            var second = new DesktopItem { Path = "second", Name = "second" };
            var surface = new System.Windows.Controls.Grid { Width = 260, Height = 160 };
            var itemCanvas = new System.Windows.Controls.Canvas();
            var marqueeCanvas = new System.Windows.Controls.Canvas { IsHitTestVisible = false };
            var marquee = new System.Windows.Controls.Border { Visibility = Visibility.Collapsed };
            var firstTile = new System.Windows.Controls.Border { Width = 40, Height = 40 };
            var secondTile = new System.Windows.Controls.Border { Width = 40, Height = 40 };
            System.Windows.Controls.Canvas.SetLeft(firstTile, 20);
            System.Windows.Controls.Canvas.SetTop(firstTile, 20);
            System.Windows.Controls.Canvas.SetLeft(secondTile, 130);
            System.Windows.Controls.Canvas.SetTop(secondTile, 20);
            itemCanvas.Children.Add(firstTile);
            itemCanvas.Children.Add(secondTile);
            marqueeCanvas.Children.Add(marquee);
            surface.Children.Add(itemCanvas);
            surface.Children.Add(marqueeCanvas);
            var window = new Window
            {
                Content = surface,
                Width = 260,
                Height = 160,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Opacity = 0,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            window.Show();
            window.UpdateLayout();
            var tiles = new Dictionary<DesktopItem, FrameworkElement>
            {
                [first] = firstTile,
                [second] = secondTile
            };
            var controller = new MarqueeSelectionController(
                surface, marquee, () => [first, second], item => tiles[item]);
            controller.Begin(new System.Windows.Point(0, 0), toggleSelection: false);
            controller.Complete(new System.Windows.Point(90, 90));
            var normalSelectionWorks = first.IsSelected && !second.IsSelected;
            controller.Begin(new System.Windows.Point(0, 0), toggleSelection: true);
            controller.Complete(new System.Windows.Point(200, 90));
            var toggleSelectionWorks = !first.IsSelected && second.IsSelected;
            var cleanupWorks = !controller.IsActive && marquee.Visibility == Visibility.Collapsed;
            window.Close();
            Shutdown(normalSelectionWorks && toggleSelectionWorks && cleanupWorks ? 0 : 13);
            return;
        }

        // 验证拖动预览跟随鼠标，同时保持穿透、不激活和工具窗口样式。
        if (e.Args.Any(argument => argument.Equals("--drag-preview-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var virtualItem = DesktopFileService.GetVisibleVirtualDesktopItems().First();
            virtualItem.Icon = new System.Windows.Media.DrawingImage(
                new System.Windows.Media.GeometryDrawing(
                    System.Windows.Media.Brushes.Gray,
                    null,
                    new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 48, 48))));
            var verified = DragPreviewWindow.ValidateForSmokeTest(virtualItem);
            Shutdown(verified ? 0 : 12);
            return;
        }

        // 验证桌面层持续接管空白区域，以支持框选和应用内部拖放。
        if (e.Args.Any(argument => argument.Equals("--desktop-drop-target-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dragState = new DesktopDragStateService();
            var surface = new DesktopSurfaceWindow(
                [],
                new DesktopSurfaceWindowCallbacks(
                    (_, _, _) => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask,
                    (_, _) => Task.FromResult(true),
                    (_, _, _) => Task.CompletedTask,
                    () => { },
                    _ => Task.CompletedTask),
                new DesktopLayerHostService(),
                dragState);
            surface.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(surface).Handle;
            var probeRegion = CreateRectRgn(0, 0, 0, 0);
            surface.SetDropTargetActive(true);
            dragState.SetActive(true);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var fullWindowActive = probeRegion != 0 && GetWindowRgn(handle, probeRegion) == 0;
            var blankScreenPoint = surface.PointToScreen(new System.Windows.Point(
                Math.Max(1, surface.ActualWidth / 2), Math.Max(1, surface.ActualHeight / 2)));
            var hitTestParameter = new nint(
                ((int)Math.Round(blankScreenPoint.Y) & 0xFFFF) << 16
                | ((int)Math.Round(blankScreenPoint.X) & 0xFFFF));
            var hitTest = SendMessage(handle, 0x0084, 0, hitTestParameter).ToInt32();
            var blankAreaCaptured = hitTest is not 0 and not -1;
            surface.SetDropTargetActive(false);
            dragState.SetActive(false);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var fullWindowRestored = probeRegion != 0 && GetWindowRgn(handle, probeRegion) == 0;
            if (probeRegion != 0) _ = DeleteObject(probeRegion);
            surface.ClosePermanently();
            Shutdown(!fullWindowActive ? 8 : !blankAreaCaptured ? 9 : !fullWindowRestored ? 10 : 0);
            return;
        }

        // 创建透明交互窗口并短暂挂入 Explorer 桌面层，验证普通应用可自然位于其上方。
        if (e.Args.Any(argument => argument.Equals("--desktop-interactive-layer-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var testWindow = new Window
            {
                Width = 120,
                Height = 80,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            var hostService = new DesktopLayerHostService();
            var desktopHostIsExplorer = ExplorerDesktopWindowService.ValidateForSmokeTest();
            var zOrderFallbackIsSafe = DesktopLayerHostService.ValidateZOrderFallbackForSmokeTest();
            var desktopLayerWindow = new Window
            {
                Width = 120,
                Height = 80,
                Left = SystemParameters.VirtualScreenLeft + 130,
                Top = SystemParameters.VirtualScreenTop,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            desktopLayerWindow.Show();
            var desktopLayerHandle = new System.Windows.Interop.WindowInteropHelper(desktopLayerWindow).Handle;
            var desktopLayerAttached = hostService.TryPlaceClosestToDesktopContent(desktopLayerHandle);
            testWindow.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(testWindow).Handle;
            var toolWindowConfigured = hostService.ConfigureAsDesktopToolWindow(handle, preventActivation: true);
            var attached = hostService.TryPlaceAboveDesktopContent(handle);
            var groupAboveDesktopLayer = hostService.IsWindowAbove(handle, desktopLayerHandle);
            var regionApplied = hostService.ApplyInteractiveRegions(handle,
                [new DesktopLayerBounds(0, 0, 48, 80)]);
            var probeRegion = CreateRectRgn(0, 0, 0, 0);
            var regionRead = probeRegion != 0 && GetWindowRgn(handle, probeRegion) > 0;
            var insideRegion = regionRead && PtInRegion(probeRegion, 24, 40);
            var outsideRegion = regionRead && !PtInRegion(probeRegion, 96, 40);
            if (probeRegion != 0) _ = DeleteObject(probeRegion);
            var fullRegionRestored = hostService.ApplyInteractiveRegions(handle, null);
            var activationEnabled = hostService.ConfigureAsDesktopToolWindow(handle, preventActivation: false);
            _ = testWindow.Activate();
            var restoredAfterActivation = hostService.TryPlaceAboveDesktopContent(handle, preventActivation: false);
            var groupStillAboveDesktopLayer = hostService.IsWindowAbove(handle, desktopLayerHandle);
            var verified = desktopHostIsExplorer && zOrderFallbackIsSafe && desktopLayerAttached.IsAttached
                           && toolWindowConfigured && attached.IsAttached
                           && hostService.IsPlacedAboveDesktopContent(handle, attached.HostHandle)
                           && groupAboveDesktopLayer
                           && regionApplied && insideRegion && outsideRegion && fullRegionRestored
                           && activationEnabled && restoredAfterActivation.IsAttached
                           && hostService.IsPlacedAboveDesktopContent(handle, restoredAfterActivation.HostHandle)
                           && groupStillAboveDesktopLayer;
            testWindow.Close();
            desktopLayerWindow.Close();
            Shutdown(verified ? 0 : 6);
            return;
        }

        // 发布前验证 Explorer 图标层可隐藏，并在任何结果下恢复可见。
        if (e.Args.Any(argument => argument.Equals("--desktop-display-layer-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            var nativeDesktop = new NativeDesktopService();
            var hostAvailable = nativeDesktop.GetDesktopHostIdentity() != IntPtr.Zero;
            var hidden = false;
            var restored = false;
            try
            {
                hidden = nativeDesktop.SetDesktopIconLayerVisible(false)
                         && !nativeDesktop.IsDesktopIconLayerVisible();
            }
            finally
            {
                restored = nativeDesktop.SetDesktopIconLayerVisible(true)
                           && nativeDesktop.IsDesktopIconLayerVisible();
            }
            Shutdown(hostAvailable && hidden && restored ? 0 : 7);
            return;
        }

        // 只读诊断入口用于发布前验证 Explorer 原生桌面能力，不创建窗口、不移动文件。
        if (e.Args.Any(argument => argument.Equals("--explorer-probe", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            var capability = new NativeDesktopService().InspectCapability();
            var probeLog = Path.Combine(Path.GetTempPath(), "MiniC-Explorer-Probe.txt");
            await File.WriteAllTextAsync(probeLog, capability.Message);
            Shutdown(capability.IsAvailable ? 0 : 3);
            return;
        }

        // 发布验证只构造窗口并解析全部 XAML 资源，不显示界面，也不接触桌面文件。
        if (e.Args.Any(argument => argument.Equals("--startup-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            if (!DesktopFallbackWatchdog.ValidateArgumentsForSmokeTest())
                throw new InvalidOperationException("Explorer 桌面回退守护参数自检失败。");
            if (!DesktopStartupReadinessService.ValidateWaitPolicyForSmokeTest())
                throw new InvalidOperationException("开机启动就绪等待策略自检失败。");
            if (!NativeDesktopService.IsAutoArrangeStyle(0x00000100)
                || NativeDesktopService.IsAutoArrangeStyle(0x00000001))
                throw new InvalidOperationException("Explorer 自动排列设置识别自检失败。");
            if (!ShellContextMenuService.ValidateDesktopBackgroundMenuForSmokeTest())
                throw new InvalidOperationException("Shell 桌面背景菜单能力自检失败。");
            if (!ClipboardItemStateService.ValidateForSmokeTest())
                throw new InvalidOperationException("复制与剪切图标状态自检失败。");
            if (!DesktopAssignmentRetentionService.ValidateForSmokeTest())
                throw new InvalidOperationException("软件更新期间桌面项目归属保留自检失败。");
            if (!DesktopItemOpenGestureTracker.ValidateForSmokeTest())
                throw new InvalidOperationException("跨窗口激活的双击打开手势自检失败。");
            LegacyStorageMigrationService.ValidateIsolatedMigration();
            await LayoutStore.ValidateLegacyPathMigrationAsync();
            var orphanedLayout = new MiniC.Models.LayoutState
            {
                LegacyAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["旧项目一.lnk"] = "legacy-one",
                    ["旧项目二.txt"] = "legacy-two"
                }
            };
            var repairedCount = LayoutRepairService.EnsureReferencedGroups(orphanedLayout, 0, 0, 1920);
            if (repairedCount != 2
                || orphanedLayout.Groups.Select(group => group.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
                throw new InvalidOperationException("旧布局孤儿归属恢复自检失败。");
            var placementItems = new[]
            {
                new DesktopItem { Path = "placement-a", Name = "A" },
                new DesktopItem { Path = "placement-b", Name = "B" },
                new DesktopItem { Path = "placement-new", Name = "New" }
            };
            var placementPositions = new Dictionary<string, MiniC.Models.DesktopIconPositionState>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["placement-a"] = new() { X = 431, Y = 317 },
                ["placement-b"] = new() { X = 513, Y = 317 }
            };
            var preferredPositions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase)
            {
                ["placement-new"] = new(431, 317)
            };
            new DesktopIconPlacementService().PlaceMissing(
                placementItems, placementPositions, preferredPositions);
            if (placementPositions["placement-a"].X != 431
                || placementPositions["placement-a"].Y != 317
                || placementPositions["placement-b"].X != 513
                || placementPositions["placement-b"].Y != 317
                || !placementPositions.ContainsKey("placement-new")
                || placementPositions["placement-new"].X == placementPositions["placement-a"].X
                   && placementPositions["placement-new"].Y == placementPositions["placement-a"].Y)
                throw new InvalidOperationException("新增桌面图标增量落位自检失败。");
            var overlapItems = new[]
            {
                new DesktopItem { Path = "overlap-existing-a", Name = "A" },
                new DesktopItem { Path = "overlap-existing-b", Name = "B" },
                new DesktopItem { Path = "overlap-new", Name = "New" }
            };
            var overlapPositions = new Dictionary<string, MiniC.Models.DesktopIconPositionState>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["overlap-existing-a"] = new() { X = 431, Y = 317 },
                ["overlap-existing-b"] = new() { X = 436, Y = 322 }
            };
            new DesktopIconPlacementService().PlaceMissing(
                overlapItems, overlapPositions,
                new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase)
                    { ["overlap-new"] = new(508, 317) });
            var overlapRectangles = overlapPositions.Values
                .Select(position => new Rect(position.X, position.Y,
                    DesktopIconPlacementService.TileWidth, DesktopIconPlacementService.TileHeight))
                .ToArray();
            if (overlapRectangles.SelectMany((left, index) =>
                    overlapRectangles.Skip(index + 1).Select(left.IntersectsWith)).Any(intersects => intersects))
                throw new InvalidOperationException("桌面图标视觉矩形避让自检失败。");
            var automaticItems = new[]
            {
                new DesktopItem { Path = "automatic-newer", Name = "B", LastWriteTicks = 2 },
                new DesktopItem { Path = "automatic-older", Name = "A", LastWriteTicks = 1 }
            };
            var automaticPositions = new Dictionary<string, MiniC.Models.DesktopIconPositionState>(
                StringComparer.OrdinalIgnoreCase);
            var randomExplorerPositions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase)
            {
                ["automatic-newer"] = new(1500, 700),
                ["automatic-older"] = new(1300, 500)
            };
            new DesktopIconPlacementService().PlaceMissing(
                automaticItems, automaticPositions, randomExplorerPositions, usePreferredPositions: false);
            if (automaticPositions.Count != 2
                || automaticPositions["automatic-older"].X != automaticPositions["automatic-newer"].X
                || automaticPositions["automatic-older"].Y >= automaticPositions["automatic-newer"].Y
                || automaticPositions["automatic-older"].X == 1300
                   && automaticPositions["automatic-older"].Y == 500)
                throw new InvalidOperationException("新建和另存为桌面项目自动排序自检失败。");
            var compactItems = new[]
            {
                new DesktopItem { Path = "compact-first", Name = "First" },
                new DesktopItem { Path = "compact-second", Name = "Second" }
            };
            var compactPositions = new Dictionary<string, MiniC.Models.DesktopIconPositionState>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["compact-first"] = new() { X = 180, Y = 192 },
                ["compact-second"] = new() { X = 344, Y = 368 }
            };
            var compacted = new DesktopIconPlacementService().AutoArrange(compactItems, compactPositions);
            if (!compacted
                || compactPositions["compact-first"].X != compactPositions["compact-second"].X
                || compactPositions["compact-first"].Y >= compactPositions["compact-second"].Y)
                throw new InvalidOperationException("MiniC 桌面自动压紧排列自检失败。");
            var virtualPlacementItem = new DesktopItem
            {
                Path = "shell:::{virtual-placement-test}",
                Name = "系统图标",
                IsVirtual = true
            };
            var virtualPlacementPositions = new Dictionary<string, MiniC.Models.DesktopIconPositionState>(
                StringComparer.OrdinalIgnoreCase)
            {
                [virtualPlacementItem.Path] = new() { X = 16, Y = 16 }
            };
            new DesktopIconPlacementService().PlaceAt(
                [virtualPlacementItem.Path], new System.Windows.Point(520, 420),
                [virtualPlacementItem], virtualPlacementPositions);
            if (virtualPlacementPositions[virtualPlacementItem.Path].X == 16
                && virtualPlacementPositions[virtualPlacementItem.Path].Y == 16)
                throw new InvalidOperationException("系统图标移动自检失败。");
            foreach (var virtualItem in DesktopFileService.GetVisibleVirtualDesktopItems())
            {
                if (ShellIconService.GetIcon(virtualItem.Path, virtualItem.IsDirectory) is null)
                    throw new InvalidOperationException($"系统桌面图标提取自检失败：{virtualItem.Name}");
            }
            if (ShellIconService.GetRecycleBinIcon(isEmpty: true) is null
                || ShellIconService.GetRecycleBinIcon(isEmpty: false) is null)
                throw new InvalidOperationException("回收站空/满系统图标提取自检失败。");
            var validationGroup = new DeskGroup { Name = "自检收纳盒" };
            var validationCallbacks = new DeskGroupWindowCallbacks(
                _ => { },
                (_, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.FromResult(true),
                (_, _, _, _) => Task.CompletedTask,
                _ => { },
                _ => { },
                _ => { },
                _ => { },
                _ => [],
                (_, _) => null,
                (_, _) => { },
                (_, _) => { },
                (_, _) => { },
                (_, _) => { },
                _ => { });
            var validationDragState = new DesktopDragStateService();
            var validationWindow = new DeskGroupWindow(
                validationGroup, validationCallbacks, new DesktopLayerHostService(), validationDragState);
            var validationSurface = new DesktopSurfaceWindow(
                [],
                new DesktopSurfaceWindowCallbacks(
                    (_, _, _) => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask,
                    (_, _, _) => Task.CompletedTask,
                    (_, _) => Task.FromResult(true),
                    (_, _, _) => Task.CompletedTask,
                    () => { },
                    _ => Task.CompletedTask),
                new DesktopLayerHostService(),
                validationDragState);
            var validationPanel = new TrayPanelWindow(
                () => { },
                _ => { },
                _ => { },
                _ => { },
                () => ("White", "Clear", 0.62),
                () => { });
            validationPanel.Close();
            validationSurface.ClosePermanently();
            validationWindow.ClosePermanently();
            Shutdown(0);
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var showPanelEvent = EventWaitHandle.OpenExisting(ShowPanelEventName);
                    showPanelEvent.Set();
                    break;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    if (attempt == 9) break;
                    Thread.Sleep(75);
                }
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DesktopFallbackWatchdog.StartForCurrentProcess();
        _showPanelEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowPanelEventName);
        var isAutoStart = e.Args.Any(argument => argument.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        var nativeDesktopService = new NativeDesktopService();
        if (isAutoStart) await DesktopStartupReadinessService.WaitAsync(nativeDesktopService);
        if (_isExiting) return;
        // 协调器创建自绘桌面图标层后，再隐藏 Explorer 的原生图标层。
        _coordinator = new DesktopCoordinator(
            new LayoutStore(),
            new DesktopFileService(),
            new LegacyStorageMigrationService(),
            new ShellContextMenuService(),
            nativeDesktopService,
            new DesktopLayerHostService(),
            () => Shutdown());
        await _coordinator.InitializeAsync();
        if (!isAutoStart) _coordinator.ShowTrayPanel();
        _ = Task.Run(WaitForPanelRequests);
    }

    private void WaitForPanelRequests()
    {
        while (!_isExiting && _showPanelEvent is not null)
        {
            try
            {
                _showPanelEvent.WaitOne();
                if (_isExiting) return;
                Dispatcher.BeginInvoke(() => _coordinator?.ShowTrayPanel());
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _coordinator?.Dispose();
        _coordinator = null;
        _showPanelEvent?.Set();
        _showPanelEvent?.Dispose();
        _showPanelEvent = null;
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint windowHandle, nint regionHandle);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint windowHandle, uint message, nint wParam, nint lParam);


    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(nint regionHandle, int x, int y);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint objectHandle);
}
