using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MiniC.Services;

namespace MiniC.Views;

/// <summary>Shared desktop-layer shell for groups and widgets.</summary>
public abstract class DesktopBoxWindow : Window
{
    private DesktopLayerHostService? _desktopLayer;

    protected DesktopBoxWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    protected void InitializeDesktopBox(DesktopLayerHostService layer)
    {
        _desktopLayer = layer ?? throw new ArgumentNullException(nameof(layer));
        PreviewMouseDown += DesktopBox_PreviewMouseDown;
        Loaded += (_, _) => { _ = PlaceAboveDesktop(force: true); };
    }

    private void DesktopBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        OnDesktopBoxInteractionStarted(e);
        ActivateDesktopBoxFromPointer();
    }

    protected virtual void OnDesktopBoxInteractionStarted(MouseButtonEventArgs e) { }

    protected virtual void ActivateDesktopBoxFromPointer() => SetDesktopBoxActivation(true);

    protected bool SetDesktopBoxActivation(bool enabled, Action? restoreIdlePlacement = null)
    {
        if (_desktopLayer is null || !IsLoaded) return false;
        return _desktopLayer.SetActivationEnabled(this, enabled,
            restoreIdlePlacement ?? (() => PlaceAboveDesktop(force: true)));
    }

    protected DesktopLayerAttachResult PlaceAboveDesktop(bool force = false)
    {
        if (_desktopLayer is null || !IsLoaded || (!force && IsActive))
            return new DesktopLayerAttachResult(false, 0, "Window is not ready for desktop placement.");
        return _desktopLayer.TryPlaceAboveDesktopContent(
            new WindowInteropHelper(this).Handle, preventActivation: false);
    }
}
