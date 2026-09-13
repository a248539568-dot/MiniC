using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MiniC.ViewModels;

namespace MiniC.Views;

/// <summary>拖动期间跟随鼠标的穿透图标预览，不参与 Drop 命中。</summary>
public sealed class DragPreviewWindow : Window
{
    private const int MaximumVisibleIcons = 64;
    private const int PreviewIconSize = 48;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);
    private readonly double _anchorX;
    private readonly double _anchorY;

    private DragPreviewWindow(
        IReadOnlyList<DesktopItem> items,
        IReadOnlyDictionary<DesktopItem, System.Windows.Point>? relativePositions)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual;

        var visibleItems = items.Take(MaximumVisibleIcons).ToArray();
        var positions = visibleItems.Select((item, index) =>
                relativePositions?.TryGetValue(item, out var position) == true
                    ? position
                    : new System.Windows.Point(index % 4 * 58, index / 4 * 58))
            .ToArray();
        var minimumX = positions.Min(position => position.X);
        var minimumY = positions.Min(position => position.Y);
        var maximumX = positions.Max(position => position.X) + PreviewIconSize;
        var maximumY = positions.Max(position => position.Y) + PreviewIconSize;
        _anchorX = positions[0].X - minimumX + PreviewIconSize / 2d;
        _anchorY = positions[0].Y - minimumY + PreviewIconSize / 2d;
        var canvas = new Canvas
        {
            Width = Math.Max(PreviewIconSize, maximumX - minimumX),
            Height = Math.Max(PreviewIconSize, maximumY - minimumY),
            IsHitTestVisible = false
        };
        Width = canvas.Width;
        Height = canvas.Height;
        for (var index = 0; index < visibleItems.Length; index++)
        {
            var image = new System.Windows.Controls.Image
            {
                Source = visibleItems[index].Icon,
                Width = PreviewIconSize,
                Height = PreviewIconSize,
                Stretch = Stretch.Uniform,
                Opacity = 0.86,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    BlurRadius = 7,
                    ShadowDepth = 2,
                    Opacity = 0.38
                }
            };
            Canvas.SetLeft(image, positions[index].X - minimumX);
            Canvas.SetTop(image, positions[index].Y - minimumY);
            canvas.Children.Add(image);
        }
        Content = canvas;
    }

    public static System.Windows.DragDropEffects Run(
        FrameworkElement source,
        IReadOnlyList<DesktopItem> items,
        System.Windows.IDataObject data,
        System.Windows.DragDropEffects allowedEffects,
        IReadOnlyDictionary<DesktopItem, System.Windows.Point>? relativePositions = null)
    {
        var preview = new DragPreviewWindow(items, relativePositions);
        System.Windows.GiveFeedbackEventHandler feedback = (_, _) => preview.UpdatePosition();
        System.Windows.QueryContinueDragEventHandler continueDrag = (_, _) => preview.UpdatePosition();
        source.GiveFeedback += feedback;
        source.QueryContinueDrag += continueDrag;
        try
        {
            preview.Show();
            preview.UpdatePosition();
            return System.Windows.DragDrop.DoDragDrop(source, data, allowedEffects);
        }
        finally
        {
            source.GiveFeedback -= feedback;
            source.QueryContinueDrag -= continueDrag;
            preview.Close();
        }
    }

    internal static bool ValidateForSmokeTest(DesktopItem item)
    {
        var preview = new DragPreviewWindow([item], null);
        try
        {
            preview.Show();
            preview.UpdatePosition();
            var handle = new WindowInteropHelper(preview).Handle;
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            if (!GetWindowRect(handle, out var bounds)) return false;
            var cursor = System.Windows.Forms.Cursor.Position;
            var dpi = VisualTreeHelper.GetDpi(preview);
            var expectedWidth = (int)Math.Round(preview.Width * dpi.DpiScaleX);
            var expectedHeight = (int)Math.Round(preview.Height * dpi.DpiScaleY);
            var anchorX = (int)Math.Round(preview._anchorX * dpi.DpiScaleX);
            var anchorY = (int)Math.Round(preview._anchorY * dpi.DpiScaleY);
            return IsWindowVisible(handle)
                   && (style & WsExTransparent) != 0
                   && (style & WsExToolWindow) != 0
                   && (style & WsExNoActivate) != 0
                   && Math.Abs(bounds.Left - (cursor.X - anchorX)) <= 2
                   && Math.Abs(bounds.Top - (cursor.Y - anchorY)) <= 2
                   && bounds.Right - bounds.Left == expectedWidth
                   && bounds.Bottom - bounds.Top == expectedHeight;
        }
        finally
        {
            preview.Close();
        }
    }

    internal static bool ValidateRelativeLayoutForSmokeTest(
        IReadOnlyList<DesktopItem> items,
        IReadOnlyDictionary<DesktopItem, System.Windows.Point> relativePositions)
    {
        var preview = new DragPreviewWindow(items, relativePositions);
        try
        {
            return preview.Width > PreviewIconSize
                   && preview.Height > PreviewIconSize
                   && Math.Abs(preview._anchorX - PreviewIconSize / 2d) < 0.1
                   && Math.Abs(preview._anchorY - PreviewIconSize / 2d) < 0.1;
        }
        finally
        {
            preview.Close();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(handle, GwlExStyle,
            new nint(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    private void UpdatePosition()
    {
        if (!IsLoaded) return;
        var handle = new WindowInteropHelper(this).Handle;
        var cursor = System.Windows.Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        var previewWidth = Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX));
        var previewHeight = Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY));
        var anchorX = (int)Math.Round(_anchorX * dpi.DpiScaleX);
        var anchorY = (int)Math.Round(_anchorY * dpi.DpiScaleY);
        _ = SetWindowPos(handle, HwndTopmost,
            cursor.X - anchorX, cursor.Y - anchorY,
            previewWidth, previewHeight,
            SwpNoActivate | SwpShowWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
