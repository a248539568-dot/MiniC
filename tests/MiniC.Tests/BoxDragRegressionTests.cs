#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using MiniC.Widgets;
using Xunit;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MiniC.Tests;

public sealed class BoxDragRegressionTests
{
    [Fact]
    public async Task LostCaptureAfterMergePreviewKeepsTheMovedPosition()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareMove();
            fixture.ArmMerge();
            fixture.Invoke("Header_LostMouseCapture", fixture.Window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
            await Task.Delay(300);
            Assert.False(fixture.Get<bool>("_returningBoxToOrigin"));
            Assert.Equal(new Point(540, 160), new Point(fixture.Window.Left, fixture.Window.Top));
            Assert.Null(fixture.Get<BoxTabRef?>("_boxDragTarget"));
            Assert.False(fixture.Get<DispatcherTimer>("_boxMergeTimer").IsEnabled);
            Assert.Null(fixture.MergedInto);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReleaseMergesOnlyIntoThePreviouslyArmedDestination(bool armed, bool changeTarget)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareMove();
            if (armed) fixture.ArmMerge();
            else fixture.Candidate = Fixture.Target;
            if (changeTarget) fixture.Candidate = new(BoxContentKind.Widget, "another-target");
            fixture.Invoke("Header_MouseLeftButtonUp", fixture.Window,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            var releasedAt = new Point(fixture.Window.Left, fixture.Window.Top);
            await Task.Delay(300);
            Assert.Equal(armed && !changeTarget ? Fixture.Target : null, fixture.MergedInto);
            Assert.False(fixture.Get<bool>("_isMovingWindow"));
            Assert.False(fixture.Get<bool>("_returningBoxToOrigin"));
            Assert.False(fixture.Get<DispatcherTimer>("_boxMergeTimer").IsEnabled);
            Assert.Equal(releasedAt, new Point(fixture.Window.Left, fixture.Window.Top));
            Assert.NotEqual(new Point(20, 30), releasedAt);
            Assert.False(fixture.Window.IsVisible);
        });
    }

    [Fact]
    public async Task EscapeStillCancelsTheMoveAndRestoresItsOrigin()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new Fixture();
            fixture.PrepareMove();
            fixture.ArmMerge();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(fixture.Window).Handle)!;
            fixture.Invoke("Window_PreviewKeyDown", fixture.Window,
                new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            await Task.Delay(300);
            Assert.Equal(new Point(20, 30), new Point(fixture.Window.Left, fixture.Window.Top));
            Assert.False(fixture.Get<DispatcherTimer>("_boxMergeTimer").IsEnabled);
            Assert.Null(fixture.MergedInto);
        });
    }

    [Fact]
    public async Task NativeTargetOrderIsIndependentOfDictionaryInsertionOrder()
    {
        await GroupMotionTests.OnUiThread(() =>
        {
            using var first = new HwndSource(new HwndSourceParameters("MiniC hidden merge target A")
                { WindowStyle = unchecked((int)0x80000000), Width = 100, Height = 100 });
            using var second = new HwndSource(new HwndSourceParameters("MiniC hidden merge target B")
                { WindowStyle = unchecked((int)0x80000000), Width = 100, Height = 100 });
            Assert.True(SetWindowPos(second.Handle, 0, 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002));
            Assert.Equal([second.Handle, first.Handle], NativeWindowPlacement.TopToBottom([first.Handle, second.Handle]));
            Assert.Equal([second.Handle, first.Handle], NativeWindowPlacement.TopToBottom([second.Handle, first.Handle]));
            Assert.False(IsWindowVisible(first.Handle));
            Assert.False(IsWindowVisible(second.Handle));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RenderReadyMergeFeedbackWithoutChangingBoxLayout()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "box-merge"));
            Directory.CreateDirectory(output);
            foreach (var (width, height, dpi) in new[] { (340, 380, 1d), (250, 170, 1.5d), (340, 42, 1d) })
            {
                using var fixture = new Fixture();
                var shell = (Border)fixture.Window.Content;
                fixture.Group.IsCollapsed = height == 42;
                shell.Measure(new Size(width, height));
                shell.Arrange(new Rect(0, 0, width, height));
                shell.UpdateLayout();
                fixture.Window.SetMergeDropTarget(true);
                await Task.Delay(240);
                shell.UpdateLayout();
                var overlay = (Border)fixture.Window.FindName("MergeDropOverlay");
                var center = BoxDropGeometry.Center(new Rect(0, 0, width, height));
                Assert.Equal(center.Width, overlay.Width, 3);
                Assert.Equal(center.Height, overlay.Height, 3);
                Assert.Equal(0, overlay.BorderThickness.Left);
                Assert.Equal(0, overlay.BorderThickness.Top);
                Assert.Equal((double)width, shell.ActualWidth);
                Assert.Equal((double)height, shell.ActualHeight);
                var label = (TextBlock)((Border)overlay.Child).Child;
                Assert.True(label.ActualHeight >= label.FontSize, "Merge instruction must remain readable in a collapsed box.");
                var bitmap = new RenderTargetBitmap((int)(width * dpi), (int)(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                bitmap.Render(shell);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"ready-{width}x{height}-{dpi * 100:0}dpi.png"));
                encoder.Save(file);
                Assert.False(fixture.Window.IsVisible);
            }
        });
    }

    [Fact]
    public async Task WholeBoxMoveKeepsOnlyTheLatestPointerPositionPerFrame()
    {
        await GroupMotionTests.OnUiThread(() =>
        {
            using var fixture = new Fixture();
            fixture.PrepareMove();
            var start = fixture.Get<Point>("_windowDragScreenStart");
            fixture.Invoke("MoveDraggedWindow", new Point(start.X + 40, start.Y + 20), true);
            fixture.Invoke("MoveDraggedWindow", new Point(start.X + 120, start.Y + 80), true);

            Assert.True(fixture.Get<bool>("_pendingDragMove"));
            Assert.Equal(120, fixture.Get<int>("_pendingDragX") - fixture.Get<Point>("_windowDragPhysicalOrigin").X);
            Assert.Equal(80, fixture.Get<int>("_pendingDragY") - fixture.Get<Point>("_windowDragPhysicalOrigin").Y);

            fixture.Invoke("RenderDraggedWindowMove", new object(), EventArgs.Empty);
            Assert.False(fixture.Get<bool>("_pendingDragMove"));
            Assert.False(fixture.Get<bool>("_dragMoveRenderingSubscribed"));
            return Task.CompletedTask;
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MiniC-BoxDrag-" + Guid.NewGuid().ToString("N"));
        internal static readonly BoxTabRef Target = new(BoxContentKind.DesktopGroup, "target");
        internal readonly DeskGroup Group = new() { Name = "工作文件", X = 20, Y = 30, Width = 340, Height = 380, Material = "Solid", Theme = "Graphite" };
        internal DeskGroupWindow Window { get; }
        internal BoxTabRef Source { get; }
        internal BoxTabRef? Candidate { get; set; }
        internal BoxTabRef? MergedInto { get; private set; }

        internal Fixture()
        {
            Source = new(BoxContentKind.DesktopGroup, Group.Id);
            var store = new WidgetStore(_root);
            var callbacks = new DeskGroupWindowCallbacks(_ => { }, (_, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.FromResult(false), (_, _, _, _) => Task.CompletedTask,
                _ => { }, _ => { }, _ => { }, _ => { }, () => { });
            Window = new DeskGroupWindow(Group, callbacks, new DesktopLayerHostService(), new DesktopDragStateService());
            var host = new BoxHostState { Id = Group.Id, Tabs = [Source], ActiveTab = Source,
                X = Group.X, Y = Group.Y, Width = Group.Width, Height = Group.Height, Material = "Solid", Theme = "Graphite" };
            Window.ConfigureHost(host, new Dictionary<string, DeskGroup> { [Group.Id] = Group },
                new Dictionary<string, WidgetState>(), store, new WidgetWeatherService(null, false),
                _ => { }, _ => { }, () => { }, (_, _) => { }, (_, target) => MergedInto = target,
                (_, _, _) => { }, (_, _) => { }, (_, _) => Candidate, (_, _) => { });
        }

        internal void PrepareMove()
        {
            Window.Left = 540;
            Window.Top = 160;
            var handle = new WindowInteropHelper(Window).EnsureHandle();
            Assert.True(NativeWindowPlacement.TryGetBounds(handle, out var bounds));
            var cursor = System.Windows.Forms.Cursor.Position;
            Set("_windowDragScreenStart", new Point(cursor.X, cursor.Y));
            Set("_windowDragPhysicalOrigin", bounds.TopLeft);
            Set("_windowDragOrigin", new Point(20, 30));
            Set("_isMovingWindow", true);
            Set("_windowDragHasMoved", true);
            Invoke("BeginBoxMergeIntent", Source, new Point(cursor.X, cursor.Y));
        }

        internal void ArmMerge()
        {
            Candidate = Target;
            var cursor = System.Windows.Forms.Cursor.Position;
            var point = new Point(cursor.X, cursor.Y);
            Get<BoxMergeGesture>("_boxMergeGesture").Update(Target, point, Environment.TickCount64 - BoxMergeGesture.DwellMilliseconds);
            Assert.Equal(Target, Invoke("UpdateBoxMergeIntent", Source, point));
        }

        internal T Get<T>(string name) => (T)typeof(DeskGroupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        private void Set(string name, object value) => typeof(DeskGroupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, value);
        internal object? Invoke(string name, params object[] args) => typeof(DeskGroupWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);

        public void Dispose()
        {
            Window.ClosePermanently();
            Directory.Delete(_root, true);
        }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
}
