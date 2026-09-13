using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MiniC.Controls;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MiniC.Views;

/// <summary>收纳盒抽离 / 融合的临时穿透预览。快照只在手势开始时生成一次。</summary>
internal sealed class GroupDragPreviewWindow : Window
{
    private readonly GroupTransitionSurface _surface;
    private TaskCompletionSource<bool>? _completion;
    private bool _closed;
    private bool _previewVisible = true;
    private readonly Rect _sourceBounds;
    private Rect _hostBounds = Rect.Empty;
    private Point? _pendingPosition;
    private bool _renderingSubscribed;

    public GroupDragPreviewWindow(FrameworkElement shell, Rect sourceBounds, Brush connectionBrush,
        double tabStripHeight = 0, System.Windows.Size? previewSize = null, FrameworkElement? body = null,
        FrameworkElement? material = null, FrameworkElement? header = null)
    {
        _sourceBounds = sourceBounds;
        shell.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(shell);
        var snapshot = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(shell.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(shell.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        snapshot.Render(shell);
        snapshot.Freeze();
        var cardSize = previewSize ?? sourceBounds.Size;
        BitmapSource cardSnapshot = snapshot;
        if (tabStripHeight > 0 && material is not null && header is not null)
        {
            // 复用实际材质层与标题层，保持主题、渐变及原透明度，不添加提亮底板。
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(material), null, new Rect(new Point(), cardSize));
                dc.DrawRectangle(new VisualBrush(header), null, new Rect(0, 0, cardSize.Width, header.ActualHeight));
                if (body is { ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible })
                {
                    var bodyPosition = body.TranslatePoint(new Point(), shell);
                    bodyPosition.Y -= tabStripHeight;
                    dc.DrawRectangle(new VisualBrush(body), null, new Rect(bodyPosition, body.RenderSize));
                }
            }
            var singleTab = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(cardSize.Width * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(cardSize.Height * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            singleTab.Render(drawing);
            singleTab.Freeze();
            cardSnapshot = singleTab;
        }
        _surface = new GroupTransitionSurface
        {
            Snapshot = cardSnapshot, CardSize = cardSize, CardPosition = sourceBounds.TopLeft,
            AnchorBounds = sourceBounds, ConnectionBrush = connectionBrush
        };
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;
        // 仅手势存续期间的穿透预览，不改变真实收纳盒的窗口层级。
        Topmost = true;
        Content = _surface;
        UpdateBounds(sourceBounds);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            _ = SetWindowLongPtr(handle, -20, new nint(style | 0x20 | 0x80 | 0x08000000));
        };
        Closed += (_, _) =>
        {
            _closed = true;
            StopFollowing();
            _surface.BeginAnimation(GroupTransitionSurface.CardPositionProperty, null);
            _surface.BeginAnimation(GroupTransitionSurface.DockProgressProperty, null);
            _completion?.TrySetResult(false);
        };
    }

    internal Rect PreviewBounds => _surface.CardBounds;
    internal bool IsFollowing => _renderingSubscribed;

    public void Follow(Point position, Rect? targetBounds = null, bool visible = true)
    {
        if (_closed || _completion is not null) return;
        if (_previewVisible != visible)
        {
            _previewVisible = visible;
            BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0,
                TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 150 : 1)));
        }
        _surface.AnchorBounds = targetBounds ?? _sourceBounds;
        _surface.IsDocking = false;
        // 合并同一帧内的鼠标输入；拖动位置直接跟手，不不断重启 EaseOut 追赶鼠标。
        _pendingPosition = position;
        if (!_renderingSubscribed)
        {
            _renderingSubscribed = true;
            CompositionTarget.Rendering += RenderPointerPosition;
        }
    }

    private void RenderPointerPosition(object? sender, EventArgs e) => FlushPointerPosition();

    private void FlushPointerPosition()
    {
        if (_pendingPosition is not { } position) return;
        _pendingPosition = null;
        UpdateBounds(new Rect(position, _surface.CardSize));
        _surface.CardPosition = position;
        _surface.InvalidateVisual();
    }

    private void StopFollowing()
    {
        if (_renderingSubscribed) CompositionTarget.Rendering -= RenderPointerPosition;
        _renderingSubscribed = false;
        _pendingPosition = null;
    }

    public Task<bool> CompleteAsync(Rect destination, bool merge)
    {
        if (_closed) return Task.FromResult(false);
        FlushPointerPosition();
        StopFollowing();
        _completion?.TrySetResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _surface.AnchorBounds = destination;
        _surface.IsDocking = merge;
        _surface.DockDirection = destination.TopLeft - _surface.CardPosition;
        UpdateBounds(destination);
        // 已在最终落点的拆出不再原地等 260ms。只有真正的回位/融合需要收尾。
        var settled = !_surface.IsDocking && (destination.TopLeft - _surface.CardPosition).Length < 0.5;
        var duration = SystemParameters.ClientAreaAnimation && !settled ? 220 : 1;
        AnimatePosition(destination.TopLeft, duration);
        var animation = new DoubleAnimation(_surface.DockProgress, merge ? 1 : 0, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) => completion.TrySetResult(!_closed);
        _surface.BeginAnimation(GroupTransitionSurface.DockProgressProperty, animation);
        return completion.Task;
    }

    private void AnimatePosition(Point position, int milliseconds)
    {
        var current = _surface.CardPosition;
        _surface.BeginAnimation(GroupTransitionSurface.CardPositionProperty, null);
        _surface.CardPosition = position;
        _surface.BeginAnimation(GroupTransitionSurface.CardPositionProperty,
            new PointAnimation(current, position,
                TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? milliseconds : 1))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void UpdateBounds(Rect destination)
    {
        var bounds = Rect.Union(_sourceBounds, _surface.CardBounds);
        bounds.Union(new Rect(destination.TopLeft, _surface.CardSize));
        bounds.Union(_surface.AnchorBounds);
        bounds.Inflate(18, 18);
        if (!_hostBounds.IsEmpty && _hostBounds.Contains(bounds)) return;
        // 保留缓冲并只扩不缩；避免每个 MouseMove 都重分配透明 HWND 的绘制表面。
        bounds.Inflate(128, 128);
        if (!_hostBounds.IsEmpty) bounds.Union(_hostBounds);
        _hostBounds = bounds;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(1, bounds.Width);
        Height = Math.Max(1, bounds.Height);
        _surface.WorldOrigin = bounds.TopLeft;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);
}
