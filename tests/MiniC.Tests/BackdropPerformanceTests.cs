using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniC.Services;
using Xunit;

namespace MiniC.Tests;

[Collection("Shared desktop renderer")]
public sealed class BackdropPerformanceTests
{
    private static readonly int[] ExtendedStyles = [0, 0x80000, 0x200000, 0x20];
    private static readonly Rect Box = new(120, 120, 340, 380);
    private static readonly BackdropWindow Host = new(1, Rect.Empty, 7);
    private static readonly BackdropWindow Opaque = new(2, new Rect(0, 0, 1920, 1080), 8)
        { OpaqueInterior = new Rect(40, 40, 1840, 1000) };

    [Fact]
    public void FullyCoveredHostSuspendsButPartialTransparentAndOwnWindowsDoNot()
    {
        Assert.False(BackdropWindowCatalog.IsExposed([Opaque, Host], 1, Box, 7));
        Assert.True(BackdropWindowCatalog.IsExposed([Host, Opaque], 1, Box, 7));
        Assert.True(BackdropWindowCatalog.IsExposed([Opaque with { OpaqueInterior = new Rect(0, 0, 200, 300) }, Host], 1, Box, 7));
        Assert.True(BackdropWindowCatalog.IsExposed([Opaque with { Opacity = 128 }, Host], 1, Box, 7));
        Assert.True(BackdropWindowCatalog.IsExposed([Opaque with { ColorKey = 0 }, Host], 1, Box, 7));
        Assert.True(BackdropWindowCatalog.IsExposed([Opaque with { OpaqueInterior = Rect.Empty }, Host], 1, Box, 7));
        Assert.True(BackdropWindowCatalog.IsExposed([Opaque with { ProcessId = 7 }, Host], 1, Box, 7));
        Assert.False(BackdropWindowCatalog.IsExposed([Opaque], 1, Box, 7));
        Assert.False(BackdropWindowCatalog.IsExposed([], 1, Box, 7));
        Assert.True(BackdropWindowCatalog.IsExposed([], 0, Box, 7));
    }

    [Fact]
    public void RearSelectionStopsAtOpaqueCoverageButPreservesNearerTransparencyAndSampleMargin()
    {
        var tint = new BackdropWindow(3, Box, 9, 128);
        var desktop = new BackdropWindow(4, new Rect(0, 0, 3840, 2160), 10);
        Assert.Equal(new[] { Opaque, tint }, BackdropWindowCatalog.Behind([Host, tint, Opaque, desktop], 1, Box, 7));
        Assert.Equal(new[] { desktop, tint }, BackdropWindowCatalog.Behind([Host, tint, desktop], 1, Box, 7));
        var keyed = Opaque with { ColorKey = 0 };
        Assert.Equal(new[] { desktop, keyed, tint }, BackdropWindowCatalog.Behind([Host, tint, keyed, desktop], 1, Box, 7));
        var inset = Opaque with { OpaqueInterior = new Rect(121, 121, 338, 378) };
        Assert.Equal(new[] { desktop, inset }, BackdropWindowCatalog.Behind([Host, inset, desktop], 1, Box, 7));
        Assert.Empty(BackdropWindowCatalog.Behind([Opaque, Host, desktop], 99, Box, 7));
    }

    [Fact]
    public void StationaryCatalogIsSampledTenTimesInsteadOfOnEveryCapturePoll()
    {
        var calls = 0;
        var snapshot = new BackdropWindowSnapshot(() => { calls++; return [Opaque, Host]; });
        BackdropWindow[] first = [];
        for (var now = 1000; now < 2000; now += 4)
        {
            var current = snapshot.Get(now, moving: false);
            if (now == 1000) first = current;
            if (now == 1096) Assert.Same(first, current);
        }
        Assert.Equal(10, calls);
        snapshot.Get(2000, moving: false);
        Assert.Equal(11, calls);
        snapshot.Get(0, moving: false);
        Assert.Equal(12, calls);
    }

    [Fact]
    public void DraggingRefreshesTheCatalogPromptlyAndReturnsToIdleCadence()
    {
        var calls = 0;
        var snapshot = new BackdropWindowSnapshot(() => { calls++; return []; });
        snapshot.Get(1000, moving: false);
        snapshot.Get(1015, moving: true);
        Assert.Equal(1, calls);
        snapshot.Get(1016, moving: true);
        Assert.Equal(2, calls);
        snapshot.Get(1115, moving: false);
        Assert.Equal(2, calls);
        snapshot.Get(1116, moving: false);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NativeOcclusionUsesClientInteriorAndRejectsLayeredOrDirectCompositionWindows()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            foreach (var extended in ExtendedStyles)
            {
                var window = new System.Windows.Forms.NativeWindow();
                try
                {
                    window.CreateHandle(new System.Windows.Forms.CreateParams
                    {
                        Caption = "MiniC hidden occlusion classification", Style = 0x00CF0000, ExStyle = extended,
                        X = -10000, Y = -10000, Width = 800, Height = 600
                    });
                    var interior = BackdropWindowCatalog.ReadOpaqueInterior(window.Handle);
                    if (extended == 0)
                    {
                        Assert.False(interior.IsEmpty);
                        Assert.InRange(interior.Width, 1, 736);
                        Assert.InRange(interior.Height, 1, 536);
                    }
                    else Assert.True(interior.IsEmpty, $"Requested={extended:X}, actual={GetWindowLongPtr(window.Handle, -20):X}, interior={interior}");
                }
                finally { window.DestroyHandle(); }
            }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task HiddenHostRetainsItsSubscriptionWithoutCapturingOrPresenting()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var host = new HwndSource(new HwndSourceParameters("MiniC hidden capture suspension test")
                { WindowStyle = unchecked((int)0x80000000), Width = 340, Height = 380 });
            var service = GpuGlassRenderService.Shared;
            var callback = false;
            using (var subscription = service.Register(Dispatcher.CurrentDispatcher, (_, _) => callback = true,
                foreground: host.Handle, hideFromCapture: false))
            {
                subscription.Update(new GpuGlassPose(120, 120, 340, 380, 1, 16, 1, 0, 0.35f, 1));
                await Until(() => service.Status.Clients == 1 && service.Status.RenderingClients == 0);
                var before = service.Status;
                await Task.Delay(240);
                Assert.Equal(0, service.Status.CapturedOutputs);
                Assert.Equal(before.Copies, service.Status.Copies);
                Assert.Equal(before.Presents, service.Status.Presents);
                Assert.False(callback);
            }
            await Until(() => service.Status.Clients == 0);
        });
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(20);
        Assert.True(condition());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
}
