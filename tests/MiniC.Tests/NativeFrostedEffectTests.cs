using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MiniC.Services;
using Windows.UI.Composition;
using Xunit;

namespace MiniC.Tests;

public sealed class NativeFrostedEffectTests
{
    [Fact]
    public async Task NativeGaussianGraphCompilesAndAcceptsLiveBlurChangesWithoutCreatingWindows()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var options = new DispatcherOptions { Size = Marshal.SizeOf<DispatcherOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            try
            {
                using var compositor = new Compositor();
                using var backdrop = compositor.CreateHostBackdropBrush();
                using var brush = CompositionGaussianBlur.CreateBrush(compositor, backdrop, 24);
                Assert.IsType<CompositionBackdropBrush>(brush.GetSourceParameter("Backdrop"));
                brush.Properties.InsertScalar("Diffusion.BlurAmount", 4);
                Assert.Equal(CompositionGetValueStatus.Succeeded, brush.Properties.TryGetScalar("Diffusion.BlurAmount", out var value));
                Assert.Equal(4, value);
                brush.Properties.InsertScalar("Diffusion.BlurAmount", 0);
            }
            finally { Marshal.Release(controller); }
            await Task.CompletedTask;
        });
    }

    [StructLayout(LayoutKind.Sequential)] private struct DispatcherOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherOptions options, out nint controller);
}
