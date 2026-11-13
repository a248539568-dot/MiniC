using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MiniC.Controls;
using MiniC.Services;
using MiniC.Views;
using Xunit;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;

namespace MiniC.Tests;

public sealed class GroupMotionTests
{
    private static readonly int[] HeaderSampleRows = [5, 20, 38];
    [Fact]
    public async Task DetachedHeaderUsesExactBoundsWithoutGhostLayers()
    {
        await OnUiThread(async () =>
        {
            var material = new Grid();
            material.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(80, 70, 120, 170)) });
            // 一个越出自身布局边界的光泽，不能改变 VisualBrush 的自动取样范围。
            var sheen = new Border { Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 8 } };
            material.Children.Add(sheen);
            var header = new Border { Height = 42, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)) };
            var marker = new Border { Width = 20, Height = 20, Background = Brushes.Red,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var body = new Border { Margin = new Thickness(9, 82, 7, 11), Background = Brushes.Transparent, Child = marker };
            var shell = new Grid { Width = 300, Height = 200 };
            shell.Children.Add(material); shell.Children.Add(header); shell.Children.Add(body);
            shell.Measure(new Size(300, 200)); shell.Arrange(new Rect(0, 0, 300, 200)); shell.UpdateLayout();
            var normal = new GroupDragPreviewWindow(shell, new Rect(0, 0, 300, 200), Brushes.Transparent);
            var detached = new GroupDragPreviewWindow(shell, new Rect(0, 0, 300, 200), Brushes.Transparent,
                34, new Size(300, 200), body, material, header);
            try
            {
                var expected = (System.Windows.Media.Imaging.BitmapSource)((GroupTransitionSurface)normal.Content).Snapshot;
                var actual = (System.Windows.Media.Imaging.BitmapSource)((GroupTransitionSurface)detached.Content).Snapshot;
                foreach (var y in HeaderSampleRows)
                {
                    var first = new byte[4]; var second = new byte[4];
                    expected.CopyPixels(new Int32Rect(50, y, 1, 1), first, 4, 0);
                    actual.CopyPixels(new Int32Rect(50, y, 1, 1), second, 4, 0);
                    for (var channel = 0; channel < 4; channel++) Assert.InRange(Math.Abs(first[channel]-second[channel]), 0, 2);
                }
                var surface = (GroupTransitionSurface)detached.Content;
                var bodyPixel = new byte[4];
                actual.CopyPixels(new Int32Rect(15, 54, 1, 1), bodyPixel, 4, 0);
                Assert.True(bodyPixel[2] > 240 && bodyPixel[1] < 10, "Body offset must be applied once after removing the tab strip.");
                surface.PreviewScale = 0.5;
                Assert.Equal(GroupGeometry.Radius * 0.5, surface.CardRadius);
            }
            finally { normal.Close(); detached.Close(); }
            await Task.CompletedTask;
        });
    }





    [Fact]
    public void DragSourceVisibilityRestoresAllSelectedItemsEvenOnCancellation()
    {
        var first = new MiniC.ViewModels.DesktopItem { Path = "first", Name = "first" };
        var second = new MiniC.ViewModels.DesktopItem { Path = "second", Name = "second", IsSelected = true };
        try
        {
            using var scope = new DragSourceVisibilityScope([first, second]);
            Assert.True(first.IsDragPreviewSource);
            Assert.True(second.IsDragPreviewSource);
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { }
        Assert.False(first.IsDragPreviewSource);
        Assert.False(second.IsDragPreviewSource);
        Assert.True(second.IsSelected);
    }

    [Fact]
    public void ReorderSlotsDoNotOscillateAtSharedEdge()
    {
        var tracker = new ReorderSlotTracker();
        (int Index, Rect Bounds)[] slots = [(0, new Rect(0, 0, 80, 88)), (1, new Rect(80, 0, 80, 88))];
        Assert.Equal(0, tracker.Select(slots, new Point(40, 44), false));
        for (var i = 0; i < 30; i++) Assert.Equal(0, tracker.Select(slots, new Point(i % 2 == 0 ? 79 : 81, 44), false));
        Assert.Equal(1, tracker.Select(slots, new Point(105, 44), false));
        Assert.Equal(1, tracker.Select(slots, new Point(79, 44), false));
        Assert.Equal(0, tracker.Select(slots, new Point(55, 44), false));
        tracker.Reset();
        Assert.Equal(1, tracker.Select(slots, new Point(81, 44), false));
    }

    [Fact]
    public async Task ReorderUsesLayoutAndRetargetsFromVisiblePositionEvenWhenInvalidated()
    {
        await OnUiThread(async () =>
        {
            var panel = new Canvas { Width = 400, Height = 200 };
            var child = new Border { Width = 80, Height = 88 };
            panel.Children.Add(child);
            panel.Measure(new Size(400, 200)); panel.Arrange(new Rect(0, 0, 400, 200));
            child.RenderTransform = new TranslateTransform(45, 20);
            panel.UpdateLayout();
            Assert.Equal(new Rect(0, 0, 80, 88), ReorderSlotTracker.LayoutBounds(child, panel));
            var before = child.TranslatePoint(new Point(), panel);
            child.InvalidateArrange();
            var captured = PanelMotionAnimator.Capture(panel);
            Assert.True(captured.ContainsKey(child));
            Canvas.SetLeft(child, 120); panel.UpdateLayout();
            PanelMotionAnimator.Animate(panel, captured);
            panel.UpdateLayout();
            if (SystemParameters.ClientAreaAnimation)
                Assert.True((child.TranslatePoint(new Point(), panel) - before).Length < 1,
                    $"before={before}, after={child.TranslatePoint(new Point(), panel)}, captured={captured[child]}, offset={VisualTreeHelper.GetOffset(child)}, transform={child.RenderTransform}");
            await Task.CompletedTask;
        });
    }


    [Theory]
    [InlineData(66, 27, 30, 41)]
    [InlineData(112, 220, 247, 238)]
    [InlineData(48, 247, 250, 255)]
    public async Task PreviewPreservesSourceColorAndAlpha(int alpha, int red, int green, int blue)
    {
        await OnUiThread(async () =>
        {
            var color = System.Windows.Media.Color.FromArgb((byte)alpha, (byte)red, (byte)green, (byte)blue);
            var material = new Border { Background = new SolidColorBrush(color) };
            var header = new Border { Height = 42, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
            var shell = new Grid { Width = 300, Height = 200 };
            shell.Children.Add(material); shell.Children.Add(header);
            shell.Measure(new Size(300, 200)); shell.Arrange(new Rect(0, 0, 300, 200)); shell.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(shell);
            var expected = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(300 * dpi.DpiScaleX), (int)Math.Ceiling(200 * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            expected.Render(shell);
            var referencePixel = new byte[4];
            expected.CopyPixels(new Int32Rect(100, 100, 1, 1), referencePixel, 4, 0);
            for (var mode = 0; mode < 2; mode++)
            {
                var preview = new GroupDragPreviewWindow(shell, new Rect(0, 0, 300, 200), material.Background,
                    mode == 1 ? 34 : 0, new Size(300, 200), null, material, header);
                try
                {
                    var snapshot = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(
                        Assert.IsType<GroupTransitionSurface>(preview.Content).Snapshot);
                    var actualPixel = new byte[4];
                    snapshot.CopyPixels(new Int32Rect(100, 100, 1, 1), actualPixel, 4, 0);
                    Assert.Equal(referencePixel, actualPixel);
                }
                finally { preview.Close(); }
            }
            await Task.CompletedTask;
        });
    }


    [Fact]
    public async Task UnchangedArrangeDoesNotRestartTabMotion()
    {
        await OnUiThread(async () =>
        {
            var panel = new Canvas { Width = 400, Height = 100 };
            var child = new Border { Width = 60, Height = 40 };
            panel.Children.Add(child);
            panel.Measure(new Size(400, 100));
            panel.Arrange(new Rect(0, 0, 400, 100));
            var previous = PanelMotionAnimator.Capture(panel);
            Canvas.SetLeft(child, 150);
            panel.UpdateLayout();
            PanelMotionAnimator.Animate(panel, previous);
            var running = child.RenderTransform;
            await Task.Delay(25);
            for (var i = 0; i < 15; i++)
            {
                var unchanged = PanelMotionAnimator.Capture(panel);
                panel.InvalidateArrange();
                panel.UpdateLayout();
                PanelMotionAnimator.Animate(panel, unchanged);
                Assert.Same(running, child.RenderTransform);
            }
        });
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void HorizontalDragStaysReorderEvenAfterLeavingBox(double x)
    {
        var mode = TabDragPolicy.ResolveMode(TabDragMode.Pending, new Vector(x, 3));
        Assert.Equal(TabDragMode.Reorder, mode);
        mode = TabDragPolicy.ResolveMode(mode, new Vector(x * 20, 500));
        Assert.Equal(TabDragMode.Reorder, mode);
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public void VerticalDragTransfersWithoutChangingTabOrderAndCanReturn(double y)
    {
        var mode = TabDragPolicy.ResolveMode(TabDragMode.Pending, new Vector(2, y));
        Assert.Equal(TabDragMode.Transfer, mode);
        mode = TabDragPolicy.ResolveMode(mode, new Vector(500, y));
        Assert.Equal(TabDragMode.Transfer, mode);
    }

    [Fact]
    public void DetachedPreviewBeginsOutsideTabBarButCommitStillRequiresOutsideHost()
    {
        var tabs = new Rect(100, 142, 300, 34);
        var host = new Rect(100, 100, 300, 300);
        var insideBody = new Point(210, 205);
        Assert.False(TabDragPolicy.ShowDetachedPreview(TabDragMode.Reorder, tabs, insideBody));
        Assert.False(TabDragPolicy.ShowDetachedPreview(TabDragMode.Transfer, tabs, new Point(210, 154)));
        Assert.False(TabDragPolicy.ShowDetachedPreview(TabDragMode.Transfer, Rect.Empty, insideBody));
        Assert.True(TabDragPolicy.ShowDetachedPreview(TabDragMode.Transfer, tabs, insideBody));
        Assert.True(host.Contains(insideBody));
        Assert.True(TabDragPolicy.ShowDetachedPreview(TabDragMode.Transfer, tabs, new Point(410, 205)));
        Assert.False(host.Contains(new Point(410, 205)));
    }

    [Theory]
    [InlineData(6, 8)]
    [InlineData(12, 12)]
    [InlineData(-12, 12)]
    public void JitterAndAmbiguousDiagonalCannotDetachOrMerge(double x, double y)
    {
        var mode = TabDragPolicy.ResolveMode(TabDragMode.Pending, new Vector(x, y));
        Assert.Equal(TabDragMode.Pending, mode);
    }

    [Theory]
    [InlineData(-1800, 300)]
    [InlineData(2000, -500)]
    [InlineData(9000, 9000)]
    public void DetachPreservesSizeAndKeepsHeaderInDesktop(double x, double y)
    {
        var desktop = new Rect(-1280, 0, 3200, 1080);
        var bounds = GroupDetachPlacement.GetBounds(new Point(x, y), new Size(380, 320), desktop);
        Assert.Equal(new Size(380, 320), bounds.Size);
        Assert.True(desktop.Contains(new Rect(bounds.X, bounds.Y, bounds.Width, 42)));
    }

    [Fact]
    public void DetachPreviewAndCommittedHostKeepTheOriginalGrabOffset()
    {
        var desktop = new Rect(0, 0, 1920, 1080);
        var size = new Size(380, 320);
        var grabOffset = new Vector(92, 66);
        var pointer = new Point(800, 500);
        var preview = GroupDetachPlacement.GetBounds(pointer, size, grabOffset, desktop);
        var committed = GroupDetachPlacement.GetBounds(pointer, size, grabOffset, desktop);
        Assert.Equal(new Rect(708, 434, 380, 320), preview);
        Assert.Equal(preview, committed);
        Assert.Equal(pointer, GroupDetachPlacement.Anchor(preview, grabOffset));
    }

    [Fact]
    public void ClampedDetachPreviewKeepsAnchorInsideTheNewHost()
    {
        var desktop = new Rect(0, 0, 1200, 800);
        var bounds = GroupDetachPlacement.GetBounds(new Point(1180, 500),
            new Size(380, 320), new Vector(90, 25), desktop);
        Assert.Equal(new Rect(820, 475, 380, 320), bounds);
        Assert.Equal(new Point(910, 500), GroupDetachPlacement.Anchor(bounds, new Vector(90, 25)));
    }

    [Fact]
    public void OversizedGroupDoesNotThrowWhenDesktopShrinks()
    {
        var bounds = GroupDetachPlacement.GetBounds(new Point(500, 200), new Size(1400, 600),
            new Rect(0, 0, 1024, 768));
        Assert.Equal(0, bounds.X);
        Assert.Equal(1400, bounds.Width);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public async Task ConnectionBreaksAtDistanceInAllDirections(int x, int y)
    {
        await OnUiThread(async () =>
        {
            var source = new Rect(0, 0, 300, 300);
            Assert.NotNull(GroupTransitionSurface.CreateConnection(source, new Rect(x * 330, y * 330, 300, 300)));
            Assert.Null(GroupTransitionSurface.CreateConnection(source, new Rect(x * 480, y * 480, 300, 300)));
            Assert.Null(GroupTransitionSurface.CreateConnection(new Rect(0, 0, 300, 42), source));
            await Task.CompletedTask;
        });
    }


    internal static async Task OnUiThread(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
