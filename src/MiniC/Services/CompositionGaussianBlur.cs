using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Effects;
using Windows.UI.Composition;

namespace MiniC.Services;

/// <summary>Direct2D Gaussian blur graph, using the Windows SDK effect interop ABI.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class CompositionGaussianBlur : IGraphicsEffect, IGraphicsEffectSource, IGraphicsEffectD2D1Interop, ICustomQueryInterface
{
    private readonly IGraphicsEffectSource _source = new CompositionEffectSourceParameter("Backdrop");
    private readonly float _deviation;
    public string Name { get; set; } = "Diffusion";

    private CompositionGaussianBlur(double radius) => _deviation = StandardDeviation(radius);

    internal static float StandardDeviation(double radius) =>
        (float)(Math.Clamp(double.IsFinite(radius) ? radius : 24, 0, 40) / 3);

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint pointer)
    {
        pointer = 0;
        if (iid != typeof(IGraphicsEffectD2D1Interop).GUID) return CustomQueryInterfaceResult.NotHandled;
        pointer = Marshal.GetComInterfaceForObject(this, typeof(IGraphicsEffectD2D1Interop), CustomQueryInterfaceMode.Ignore);
        return CustomQueryInterfaceResult.Handled;
    }

    internal static CompositionEffectBrush CreateBrush(Compositor compositor, CompositionBrush backdrop, double radius)
    {
        var graph = new CompositionGaussianBlur(radius);
        using var factory = compositor.CreateEffectFactory(graph, ["Diffusion.BlurAmount"]);
        var brush = factory.CreateBrush();
        try
        {
            brush.SetSourceParameter("Backdrop", backdrop);
            return brush;
        }
        catch { brush.Dispose(); throw; }
    }

    public void GetEffectId(out Guid id) => id = new Guid("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
    public void GetNamedPropertyMapping(string name, out uint index, out uint mapping)
    {
        if (name != "BlurAmount") throw new ArgumentException("Unknown effect property.", nameof(name));
        index = 0;
        mapping = 1; // GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT
    }
    public void GetPropertyCount(out uint count) => count = 3;
    public void GetProperty(uint index, out nint value)
    {
        object property = index switch
        {
            0 => PropertyValue.CreateSingle(_deviation),
            1 => PropertyValue.CreateUInt32(1), // Balanced optimization
            2 => PropertyValue.CreateUInt32(1), // Hard border
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        var inspectable = WinRT.MarshalInspectable<object>.FromManaged(property);
        try
        {
            // The ABI requires IPropertyValue itself, not its IInspectable pointer.
            var id = new Guid("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in id, out value));
        }
        finally { Marshal.Release(inspectable); }
    }
    public void GetSource(uint index, out nint source)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(index, 0u);
        source = WinRT.MarshalInterface<IGraphicsEffectSource>.FromManaged(_source);
    }
    public void GetSourceCount(out uint count) => count = 1;
}

[ComImport, Guid("2FC57384-A068-44D7-A331-30982FCF7177"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsEffectD2D1Interop
{
    void GetEffectId(out Guid id);
    void GetNamedPropertyMapping([MarshalAs(UnmanagedType.LPWStr)] string name, out uint index, out uint mapping);
    void GetPropertyCount(out uint count);
    void GetProperty(uint index, out nint value);
    void GetSource(uint index, out nint source);
    void GetSourceCount(out uint count);
}
