using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniC.Controls;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using Xunit;
using Size = System.Windows.Size;

namespace MiniC.Tests;

public sealed class WindowPresentationTests
{
    private static readonly string[] Materials = ["Crystal", "Frosted", "Solid"];
    private static readonly int[] Heights = [380, 42];
    private static readonly string[] Surfaces = ["CrystalBackdrop", "FrostedBackdrop", "SolidBackdrop"];

    [Fact]
    public async Task ResizeHandleHasALargerHitAreaAndKeepsItsOriginalGlyphSize()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var group = new DeskGroup { Width = 340, Height = 380 };
            var callbacks = new DeskGroupWindowCallbacks(_ => { }, (_, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.FromResult(false), (_, _, _, _) => Task.CompletedTask,
                _ => { }, _ => { }, _ => { }, _ => { }, () => { });
            var window = new DeskGroupWindow(group, callbacks, new DesktopLayerHostService(), new DesktopDragStateService());
            try
            {
                var shell = (Border)window.Content;
                shell.Measure(new Size(340, 380));
                shell.Arrange(new Rect(0, 0, 340, 380));
                shell.UpdateLayout();
                var handle = (Thumb)window.FindName("ResizeHandle");
                Assert.Equal(new Size(32, 32), handle.RenderSize);
                Assert.NotNull(VisualTreeHelper.HitTest(handle, new System.Windows.Point(2, 2)));
                Assert.NotNull(VisualTreeHelper.HitTest(handle, new System.Windows.Point(29, 29)));
                var surface = Assert.IsType<Border>(VisualTreeHelper.GetChild(handle, 0));
                var glyph = Assert.IsType<Viewbox>(surface.Child);
                Assert.Equal(new Size(11, 11), glyph.RenderSize);
                handle.RaiseEvent(new DragDeltaEventArgs(25, 17) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.Equal(365, window.Width);
                Assert.Equal(397, window.Height);
                handle.RaiseEvent(new DragDeltaEventArgs(-1000, -1000) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.Equal(250, window.Width);
                Assert.Equal(170, window.Height);
                Assert.False(window.IsVisible);
                await Task.CompletedTask;
            }
            finally { window.ClosePermanently(); }
        });
    }
    [Fact]
    public async Task DisabledModalOwnerKeepsItsZOrderAndActivationStyle()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var owner = new HwndSource(new HwndSourceParameters("MiniC hidden modal owner")
            {
                WindowStyle = unchecked((int)0x88000000), // WS_POPUP | WS_DISABLED, never visible.
                ExtendedWindowStyle = 0x08000080, Width = 100, Height = 100
            });
            var beforeStyle = GetWindowLongPtr(owner.Handle, -20);
            var beforeAbove = GetWindow(owner.Handle, 3);
            var beforeBelow = GetWindow(owner.Handle, 2);
            var layer = new DesktopLayerHostService();
            Assert.False(layer.TryPlaceAboveDesktopContent(owner.Handle, preventActivation: false).IsAttached);
            Assert.False(layer.TryPlaceClosestToDesktopContent(owner.Handle, preventActivation: false).IsAttached);
            Assert.Equal(beforeStyle, GetWindowLongPtr(owner.Handle, -20));
            Assert.Equal(beforeAbove, GetWindow(owner.Handle, 3));
            Assert.Equal(beforeBelow, GetWindow(owner.Handle, 2));
            Assert.False(IsWindowVisible(owner.Handle));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DefaultCornersMatchShellMaterialsAndPreviewAtDifferentHeights()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.LightGray, null, new Rect(0, 0, 1060, 470));
                var column = 0;
                foreach (var mode in Materials)
                {
                    var group = new DeskGroup { Name = mode, Material = mode, Width = 340, Height = 380 };
                    group.Appearance = group.Appearance with { SeeThroughWindows = true };
                    var callbacks = new DeskGroupWindowCallbacks(_ => { }, (_, _, _) => Task.CompletedTask,
                        (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                        (_, _, _) => Task.FromResult(false), (_, _, _, _) => Task.CompletedTask,
                        _ => { }, _ => { }, _ => { }, _ => { }, () => { });
                    var window = new DeskGroupWindow(group, callbacks, new DesktopLayerHostService(), new DesktopDragStateService());
                    try
                    {
                        var shell = (Border)window.Content;
                        foreach (var height in Heights)
                        {
                            group.IsCollapsed = height == 42;
                            shell.Measure(new Size(340, height));
                            shell.Arrange(new Rect(0, 0, 340, height));
                            shell.UpdateLayout();
                            Assert.Equal(new CornerRadius(16), shell.CornerRadius);
                            var material = (Grid)window.FindName("GroupMaterial");
                            Assert.Equal(16, Assert.IsType<RectangleGeometry>(material.Clip).RadiusX);
                            foreach (var name in Surfaces)
                                Assert.Equal(16, ((GlassMaterialSurface)window.FindName(name)).Radius);
                            var bitmap = new RenderTargetBitmap(340, height, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(shell);
                            var corner = new byte[4];
                            bitmap.CopyPixels(new Int32Rect(2, 2, 1, 1), corner, 4, 0);
                            Assert.Equal(0, corner[3]);
                            dc.DrawImage(bitmap, new Rect(10 + column * 350, height == 380 ? 10 : 412, 340, height));
                        }
                        Assert.False(window.IsVisible);
                    }
                    finally { window.ClosePermanently(); }
                    column++;
                }
            }
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "window-corners.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var sheet = new RenderTargetBitmap(1060, 470, 96, 96, PixelFormats.Pbgra32);
            sheet.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheet));
            using var file = File.Create(output);
            encoder.Save(file);
            await Task.CompletedTask;
        });
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
}
