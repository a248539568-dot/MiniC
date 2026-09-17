using System;
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
    public async Task PointerBurstsUseLatestFrameWithoutChasingAnimationOrRepeatedResize()
    {
        await OnUiThread(async () =>
        {
            var shell = new Border { Width = 300, Height = 200, Background = Brushes.White };
            shell.Measure(new Size(300, 200));
            shell.Arrange(new Rect(0, 0, 300, 200));
            var preview = new GroupDragPreviewWindow(shell, new Rect(-12000, -12000, 300, 200), Brushes.LightGray);
            try
            {
                preview.Show();
                await NextFrame();
                var originalBounds = new Rect(preview.Left, preview.Top, preview.Width, preview.Height);
                for (var i = 1; i <= 80; i++) preview.Follow(new Point(-12000 + i, -12000 + i));
                await NextFrame();
                Assert.Equal(new Point(-11920, -11920), preview.PreviewBounds.TopLeft);
                Assert.Equal(originalBounds, new Rect(preview.Left, preview.Top, preview.Width, preview.Height));
                var surface = Assert.IsType<GroupTransitionSurface>(preview.Content);
                Assert.False(DependencyPropertyHelper.GetValueSource(surface, GroupTransitionSurface.CardPositionProperty).IsAnimated);
                preview.Follow(new Point(-11800, -11900));
                var completion = preview.CompleteAsync(new Rect(-11800, -11900, 300, 200), false);
                Assert.False(preview.IsFollowing);
                Assert.True(await completion);
                Assert.Equal(new Point(-11800, -11900), preview.PreviewBounds.TopLeft);
                preview.Close();
                Assert.False(preview.IsFollowing);
            }
            finally { if (preview.IsVisible) preview.Close(); }
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

    private static async Task NextFrame()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = null;
        handler = (_, _) => { CompositionTarget.Rendering -= handler; rendered.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { CompositionTarget.Rendering -= handler; }
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
        Assert.Equal(TabDropIntent.Reorder, TabDragPolicy.ResolveIntent(mode, false, true));
        Assert.Equal(TabDropIntent.Reorder, TabDragPolicy.ResolveIntent(mode, false, false));
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public void VerticalDragTransfersWithoutChangingTabOrderAndCanReturn(double y)
    {
        var mode = TabDragPolicy.ResolveMode(TabDragMode.Pending, new Vector(2, y));
        Assert.Equal(TabDragMode.Transfer, mode);
        Assert.Equal(TabDropIntent.Detach, TabDragPolicy.ResolveIntent(mode, false, false));
        mode = TabDragPolicy.ResolveMode(mode, new Vector(500, y));
        Assert.Equal(TabDragMode.Transfer, mode);
        Assert.Equal(TabDropIntent.Merge, TabDragPolicy.ResolveIntent(mode, false, true));
        Assert.Equal(TabDropIntent.Reorder, TabDragPolicy.ResolveIntent(mode, true, true));
    }

    [Theory]
    [InlineData(6, 8)]
    [InlineData(12, 12)]
    [InlineData(-12, 12)]
    public void JitterAndAmbiguousDiagonalCannotDetachOrMerge(double x, double y)
    {
        var mode = TabDragPolicy.ResolveMode(TabDragMode.Pending, new Vector(x, y));
        Assert.Equal(TabDragMode.Pending, mode);
        Assert.Equal(TabDropIntent.Reorder, TabDragPolicy.ResolveIntent(mode, false, true));
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

    [Fact]
    public async Task InterruptedMergeCannotCommitAndPreviewNeverResizesSource()
    {
        await OnUiThread(async () =>
        {
            var shell = new Border { Width = 300, Height = 200, Background = Brushes.White };
            shell.Measure(new Size(300, 200));
            shell.Arrange(new Rect(0, 0, 300, 200));
            var source = new Rect(-12000, -12000, 300, 200);
            var preview = new GroupDragPreviewWindow(shell, source, Brushes.LightGray);
            try
            {
                preview.Show();
                preview.Follow(new Point(-11650, -12000));
                var merge = preview.CompleteAsync(new Rect(-11500, -12000, 440, 320), merge: true);
                var cancel = preview.CompleteAsync(source, merge: false);
                Assert.False(await merge);
                Assert.True(await cancel);
                Assert.Equal(source, preview.PreviewBounds);
                Assert.Equal(300, shell.ActualWidth);
                Assert.Equal(200, shell.ActualHeight);
                var interruptedByClose = preview.CompleteAsync(source, merge: true);
                preview.Close();
                Assert.False(await interruptedByClose);
            }
            finally { if (preview.IsVisible) preview.Close(); }
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
