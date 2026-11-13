using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniC.Controls;
using MiniC.Services;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MiniC.Views;

/// <summary>盒子标签拆出的临时穿透预览。快照只在手势开始时生成一次。</summary>
internal sealed class GroupDragPreviewWindow : Window
{
    private readonly GroupTransitionSurface _surface;
    private bool _closed;
    private bool _previewVisible = true;
    private readonly Rect _sourceBounds;
    private Rect _hostBounds = Rect.Empty;
    private Point? _pendingPosition;
    private Vector _pendingAnchor;
    private bool _renderingSubscribed;
    private double _targetScale = 1;
    private TimeSpan? _lastFrame;
    private GlassMaterialSurface? _glassLayer;
    private System.Windows.Controls.Canvas? _glassCanvas;
    private readonly MatrixTransform _glassTransform = new();
    private nint _handle;
    private (int X, int Y, int Width, int Height)? _nativeBounds;
    private (Rect Card, Point Origin, double Scale)? _glassPose;
    private readonly bool _nativeFrostedRequested;
    private ComposedFrostedBackdrop? _composedFrostedBackdrop;
    private ComposedCrystalBackdrop? _composedCrystalBackdrop;
    private readonly bool _hideFromCapture;
    private WindowCaptureExclusion? _captureExclusion;

    public GroupDragPreviewWindow(FrameworkElement shell, Rect sourceBounds, Brush connectionBrush,
        double tabStripHeight = 0, System.Windows.Size? previewSize = null, FrameworkElement? body = null,
        FrameworkElement? material = null, FrameworkElement? header = null)
    {
        _sourceBounds = sourceBounds;
        _hideFromCapture = Window.GetWindow(shell) is DeskGroupWindow owner ? owner.Group.Appearance.HideFromCapture : true;
        shell.UpdateLayout();
        var radius = shell is System.Windows.Controls.Border border
            ? Math.Max(0, border.CornerRadius.TopLeft) : GroupGeometry.Radius;
        var panel = material as System.Windows.Controls.Panel;
        var sourceSurface = panel?.Children.OfType<GlassMaterialSurface>().FirstOrDefault(surface => surface.Visibility == Visibility.Visible);
        var separateMaterial = sourceSurface is not null;
        var sourceVisibility = sourceSurface?.Visibility;
        if (separateMaterial) sourceSurface!.SetCurrentValue(VisibilityProperty, Visibility.Hidden);
        try
        {
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
                dc.PushClip(new RectangleGeometry(new Rect(new Point(), cardSize), radius, radius));
                dc.DrawRectangle(ExactVisualBrush(material), null, new Rect(new Point(), cardSize));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, cardSize.Width, header.ActualHeight)));
                dc.DrawRectangle(ExactVisualBrush(header), null, new Rect(0, 0, cardSize.Width, header.ActualHeight));
                dc.Pop();
                if (body is { ActualWidth: > 0, ActualHeight: > 0, Visibility: Visibility.Visible })
                {
                    var bodyPosition = body.TranslatePoint(new Point(), shell);
                    bodyPosition.Y -= tabStripHeight;
                    dc.DrawRectangle(ExactVisualBrush(body), null, new Rect(bodyPosition, body.RenderSize));
                }
                dc.Pop();
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
            AnchorBounds = sourceBounds, ConnectionBrush = connectionBrush, Radius = radius
        };
        }
        finally
        {
            if (separateMaterial && sourceVisibility is { } visibility)
                sourceSurface!.SetCurrentValue(VisibilityProperty, visibility);
        }
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
        if (separateMaterial)
        {
            var layers = new System.Windows.Controls.Grid();
            _glassCanvas = new System.Windows.Controls.Canvas { IsHitTestVisible = false };
            _glassLayer = sourceSurface!.CreatePreview();
            _glassLayer.Width = _surface.CardSize.Width;
            _glassLayer.Height = _surface.CardSize.Height;
            _glassLayer.RenderTransform = _glassTransform;
            _nativeFrostedRequested = sourceSurface is FrostedGlassSurface { UseWallpaper: false };
            _glassCanvas.Children.Add(_glassLayer);
            layers.Children.Add(_glassCanvas);
            layers.Children.Add(_surface);
            Content = layers;
        }
        else Content = _surface;
        UpdateBounds(sourceBounds);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _handle = handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            _ = SetWindowLongPtr(handle, -20, new nint(style | 0x20 | 0x80 | 0x08000000));
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _composedFrostedBackdrop?.Dispose();
            _composedFrostedBackdrop = null;
            _composedCrystalBackdrop?.Dispose();
            _composedCrystalBackdrop = null;
            _captureExclusion?.Dispose();
            _captureExclusion = null;
            StopFollowing();
        };
        Loaded += (_, _) =>
        {
            if (_hideFromCapture || _glassLayer is CrystalGlassSurface { UseWallpaper: false })
            {
                try { _captureExclusion = new WindowCaptureExclusion(_handle); }
                catch (System.ComponentModel.Win32Exception error)
                { MiniCLogger.Warning(nameof(GroupDragPreviewWindow), $"Preview capture policy unavailable: {error.Message}"); }
            }
            if (_glassLayer is CrystalGlassSurface { UseWallpaper: false } crystal)
            {
                _composedCrystalBackdrop = ComposedCrystalBackdrop.TryCreate(this, crystal, _hideFromCapture, () =>
                {
                    if (_closed || _hideFromCapture) return;
                    _captureExclusion?.Dispose();
                    _captureExclusion = null;
                });
                if (_composedCrystalBackdrop is not null) _composedCrystalBackdrop.ForegroundOpacity = Opacity;
                else if (!_hideFromCapture) { _captureExclusion?.Dispose(); _captureExclusion = null; }
            }
            if (_nativeFrostedRequested && _glassLayer is FrostedGlassSurface frosted)
            {
                _composedFrostedBackdrop = ComposedFrostedBackdrop.TryCreate(this, frosted, frosted.Radius, frosted.BlurRadius, _hideFromCapture);
                if (_composedFrostedBackdrop is not null) _composedFrostedBackdrop.ForegroundOpacity = Opacity;
                frosted.NativeBackdropActive = _composedFrostedBackdrop is not null;
            }
        };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == OpacityProperty && _composedFrostedBackdrop is not null)
            _composedFrostedBackdrop.ForegroundOpacity = Opacity;
        if (args.Property == OpacityProperty && _composedCrystalBackdrop is not null)
            _composedCrystalBackdrop.ForegroundOpacity = Opacity;
    }

    private static VisualBrush ExactVisualBrush(FrameworkElement element) => new(element)
    {
        ViewboxUnits = BrushMappingMode.Absolute,
        Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(element),
            new System.Windows.Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight))),
        Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
    };

    /// <summary>Show 之前设置首帧，防止先在源盒位置闪现一张全尺寸卡片。</summary>
    public void InitializePose(Point position, Point pointer, double scale)
    {
        if (IsLoaded) throw new InvalidOperationException("Initialize the preview before showing it.");
        _surface.CardPosition = position;
        _surface.ScaleAnchor = GetAnchor(position, pointer);
        _surface.PreviewScale = Math.Clamp(scale, 0.38, 1);
        _targetScale = _surface.PreviewScale;
        UpdateBounds(new Rect(position, _surface.CardSize));
    }

    private Vector GetAnchor(Point position, Point? pointer)
    {
        var anchor = pointer is { } point ? point - position : new Vector(_surface.CardSize.Width / 2, 18);
        return new Vector(Math.Clamp(anchor.X, 0, _surface.CardSize.Width),
            Math.Clamp(anchor.Y, 0, _surface.CardSize.Height));
    }

    public void Follow(Point position, bool visible = true, double scale = 1, Point? pointer = null)
    {
        if (_closed) return;
        if (_previewVisible != visible)
        {
            _previewVisible = visible;
            BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0,
                TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 150 : 1)));
        }
        _targetScale = Math.Clamp(scale, 0.38, 1);
        _pendingAnchor = GetAnchor(position, pointer);
        // 合并同一帧内的鼠标输入；拖动位置直接跟手，不不断重启 EaseOut 追赶鼠标。
        _pendingPosition = position;
        StartRendering();
    }

    private void StartRendering()
    {
        if (!_renderingSubscribed)
        {
            _renderingSubscribed = true;
            CompositionTarget.Rendering += RenderPointerPosition;
        }
    }

    private void RenderPointerPosition(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || _lastFrame == frame.RenderingTime) return;
        var seconds = _lastFrame is { } previous ? (frame.RenderingTime - previous).TotalSeconds : 1d / 60;
        _lastFrame = frame.RenderingTime;
        FlushPointerPosition();
        var current = _surface.PreviewScale;
        var next = SystemParameters.ClientAreaAnimation
            ? current + (_targetScale - current) * (1 - Math.Exp(-Math.Clamp(seconds, 0, 0.1) / 0.055))
            : _targetScale;
        _surface.PreviewScale = Math.Abs(next - _targetScale) < 0.001 ? _targetScale : next;
        UpdateBounds(new Rect(_surface.CardPosition, _surface.CardSize));
        if (_pendingPosition is null && Math.Abs(_surface.PreviewScale - _targetScale) < 0.001)
        {
            CompositionTarget.Rendering -= RenderPointerPosition;
            _renderingSubscribed = false;
            _lastFrame = null;
        }
    }

    private void FlushPointerPosition()
    {
        if (_pendingPosition is not { } position) return;
        _pendingPosition = null;
        _surface.ScaleAnchor = _pendingAnchor;
        _surface.CardPosition = position;
        _surface.InvalidateVisual();
    }

    private void StopFollowing()
    {
        if (_renderingSubscribed) CompositionTarget.Rendering -= RenderPointerPosition;
        _renderingSubscribed = false;
        _lastFrame = null;
        _pendingPosition = null;
    }

    private void UpdateBounds(Rect destination)
    {
        var bounds = Rect.Union(_sourceBounds, _surface.CardBounds);
        bounds.Union(new Rect(destination.TopLeft, _surface.CardSize));
        bounds.Union(_surface.AnchorBounds);
        bounds.Inflate(18, 18);
        if (!_hostBounds.IsEmpty && _hostBounds.Contains(bounds))
        {
            UpdateGlassLayer();
            return;
        }
        // 保留缓冲并只扩不缩；避免每个 MouseMove 都重分配透明 HWND 的绘制表面。
        bounds.Inflate(128, 128);
        if (!_hostBounds.IsEmpty) bounds.Union(_hostBounds);
        _hostBounds = bounds;
        SetHostBounds(bounds);
        _surface.WorldOrigin = bounds.TopLeft;
        UpdateGlassLayer();
    }

    private void UpdateGlassLayer()
    {
        if (_glassLayer is null || _glassCanvas is null) return;
        var card = _surface.CardBounds;
        var pose = (card, _surface.WorldOrigin, _surface.PreviewScale);
        if (_glassPose == pose) return;
        _glassPose = pose;
        _glassTransform.Matrix = new Matrix(_surface.PreviewScale, 0, 0, _surface.PreviewScale,
            card.Left - _surface.WorldOrigin.X, card.Top - _surface.WorldOrigin.Y);
        card.Offset(-_surface.WorldOrigin.X, -_surface.WorldOrigin.Y);
        _glassCanvas.Clip = new RectangleGeometry(card, _surface.CardRadius, _surface.CardRadius);
        _glassLayer.InvalidateBackdrop();
        _composedFrostedBackdrop?.Align();
        _composedCrystalBackdrop?.Align();
    }

    private void SetHostBounds(Rect bounds)
    {
        if (_handle == 0)
        {
            Left = bounds.Left; Top = bounds.Top;
            Width = Math.Max(1, bounds.Width); Height = Math.Max(1, bounds.Height);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixels = ((int)Math.Round(bounds.Left * dpi.DpiScaleX), (int)Math.Round(bounds.Top * dpi.DpiScaleY),
            Math.Max(1, (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY)));
        if (_nativeBounds == pixels) return;
        if (SetWindowPos(_handle, 0, pixels.Item1, pixels.Item2, pixels.Item3, pixels.Item4, 0x0010 | 0x0004))
            _nativeBounds = pixels;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
