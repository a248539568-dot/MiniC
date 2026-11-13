using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniC.Controls;
using MiniC.Effects;
using MiniC.Services;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

[Collection("Shared desktop renderer")]
public sealed class CrystalGlassTests
{
    private static readonly int[] EdgeSamples = [3, 6, 12];
    [Fact]
    public async Task LensInputStaysInsideTheSurfaceAndCollapsedRadiusIsClamped()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var surface = new WallpaperGlassSurface { Width = 120, Height = 42, Radius = 32, UseWallpaper = true };
            surface.Measure(new System.Windows.Size(120, 42));
            surface.Arrange(new Rect(0, 0, 120, 42));
            surface.Strength = 1.5;
            surface.Angle = 90;
            var clip = Assert.IsType<RectangleGeometry>(surface.Clip);
            Assert.Equal(new Rect(0, 0, 120, 42), clip.Rect);
            Assert.Equal(0, clip.RadiusX);
            var effect = Assert.IsType<CrystalGlassEffect>(surface.Effect);
            Assert.Equal(new System.Windows.Point(120, 42), effect.Dimensions);
            Assert.Equal(21, effect.CornerRadius);
            Assert.Equal(1, effect.Direction.Y, 6);
            surface.BlurRadius = 18;
            Assert.IsType<System.Windows.Media.Effects.BlurEffect>(surface.Effect);
            surface.BlurRadius = 0;
            Assert.Same(effect, surface.Effect);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ResizingPreservesRefractionDistanceInPixels()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var small = RenderLens(LensTexture(256, 160), 1, 0);
            var large = RenderLens(LensTexture(384, 240), 1, 0);
            foreach (var x in EdgeSamples)
                Assert.Equal(Pixel(small, x, 80), Pixel(large, x, 80));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task CompiledLensRefractsEveryEdgeKeepsCentreSharpAndHonoursAngle()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var source = LensTexture(256, 160);
            var unchanged = RenderLens(source, 0, 0);
            var horizontal = RenderLens(source, 1, 0);
            var rotated = RenderLens(source, 1, 90);
            Assert.Equal(Pixel(unchanged, 120, 80), Pixel(horizontal, 120, 80));
            foreach (var region in new[] { new Int32Rect(2, 35, 14, 80), new Int32Rect(240, 35, 14, 80),
                new Int32Rect(35, 2, 180, 14), new Int32Rect(35, 144, 180, 14) })
                Assert.True(DifferentPixels(unchanged, horizontal, region) > 30, $"No refraction at {region}.");
            Assert.True(DifferentPixels(horizontal, rotated, new Int32Rect(2, 35, 14, 80)) > 30);
            Assert.Equal(255, Pixel(horizontal, 0, 0)[3]);
            Export("crystal-source", unchanged);
            Export("crystal-lens", horizontal);
            Export("crystal-angle-90", rotated);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ReflectionIsDirectionalAndLimitedToTheNarrowRim()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var edge = new GlassSurfaceFrame { Width = 256, Height = 160, Radius = 16 };
            edge.Measure(new System.Windows.Size(256, 160));
            edge.Arrange(new Rect(0, 0, 256, 160));
            edge.UpdateLayout();
            var live = new RenderTargetBitmap(256, 160, 96, 96, PixelFormats.Pbgra32);
            live.Render(edge);
            Assert.Equal(0, Pixel(live, 120, 80)[3]);
            Assert.True(Pixel(live, 2, 80)[3] > 0);
            Assert.Equal(0, Pixel(live, 0, 0)[3]);
            Assert.Equal(0, Pixel(live, 5, 80)[3]);
            Assert.NotEqual(Pixel(live, 100, 1), Pixel(live, 155, 158));
            edge.Angle = 180;
            edge.UpdateLayout();
            var reversed = new RenderTargetBitmap(256, 160, 96, 96, PixelFormats.Pbgra32);
            reversed.Render(edge);
            Assert.True(DifferentPixels(live, reversed, new Int32Rect(35, 0, 180, 3)) > 30);
            Export("crystal-live-edges", live);
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task RoundedLensBendsInwardInAllFourCorners(double radius)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var pixels = new byte[256 * 160 * 4];
            for (var y = 0; y < 160; y++)
                for (var x = 0; x < 256; x++)
                {
                    var i = (y * 256 + x) * 4;
                    pixels[i] = (byte)(y * 255 / 159);
                    pixels[i + 1] = 100;
                    pixels[i + 2] = (byte)x;
                    pixels[i + 3] = 255;
                }
            var source = BitmapSource.Create(256, 160, 96, 96, PixelFormats.Pbgra32, null, pixels, 256 * 4);
            var lens = RenderLens(source, 1, 0, cornerRadius: radius);
            var inset = Math.Max(8, (int)(radius / 2));
            foreach (var (x, y) in new[] { (inset, inset), (255 - inset, inset), (inset, 159 - inset), (255 - inset, 159 - inset) })
            {
                var before = Pixel(source, x, y);
                var after = Pixel(lens, x, y);
                Assert.True((after[2] - before[2]) * (x < 128 ? 1 : -1) > 3, $"Wrong X normal at ({x},{y}).");
                Assert.True((after[0] - before[0]) * (y < 80 ? 1 : -1) > 3, $"Wrong Y normal at ({x},{y}): before={before[0]}, after={after[0]}.");
            }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ProductionGlassHasAFullCentreMaskAndExportsReferenceInspiredRibbons()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            const int width = 392, height = 328;
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 20, 25)), null, new Rect(0, 0, width, height));
                for (var j = 0; j < 12; j++)
                {
                    var ribbon = new StreamGeometry();
                    using (var path = ribbon.Open())
                    {
                        path.BeginFigure(new System.Windows.Point(-40, 40 + j * 14), false, false);
                        path.BezierTo(new System.Windows.Point(70, -60 + j * 16), new System.Windows.Point(210, 270 - j * 5),
                            new System.Windows.Point(430, 100 + j * 12), true, false);
                    }
                    var color = (j % 3) switch { 0 => Colors.Coral, 1 => Colors.LightSkyBlue, _ => Colors.LightGoldenrodYellow };
                    dc.DrawGeometry(null, new System.Windows.Media.Pen(new SolidColorBrush(color), 3 + j % 4), ribbon);
                }
            }
            var source = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            source.Render(drawing);
            var glass = WallpaperRenderHost.Crystal(source);
            glass.Width = width; glass.Height = height; glass.Radius = 28;
            glass.Tint = GroupMaterialBrushes.Background("White", "Crystal", 0.62);
            glass.Measure(new System.Windows.Size(width, height));
            glass.Arrange(new Rect(0, 0, width, height));
            glass.UpdateLayout();
            using var host = await WallpaperRenderHost.Load(glass, glass);
            Assert.NotNull(glass.OpacityMask);
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(glass);
            Assert.Equal(255, Pixel(image, width / 2, height / 2)[3]);
            Assert.Equal(0, Pixel(image, 0, 0)[3]);
            Assert.True(DifferentPixels(source, image, new Int32Rect(3, 45, 26, 210)) > 100);
            Export("ribbons-source", source);
            Export("ribbons-glass", image);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RippleParametersDisplaceTheWholePaneAndKeepTextureSharp()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var texture = LensTexture(256, 160);
            var still = RenderLens(texture, 1, 0);
            var wave = RenderLens(texture, 1, 0, rippleStrength: 1);
            var widerWave = RenderLens(texture, 1, 0, rippleStrength: 1, rippleScale: 2);
            var edge = new Int32Rect(2, 35, 14, 80);
            Assert.True(DifferentPixels(still, wave, edge) > 30);
            Assert.True(DifferentPixels(wave, widerWave, edge) > 30);
            var centre = new Int32Rect(50, 45, 150, 70);
            Assert.True(DifferentPixels(still, wave, centre) > 1000);
            Assert.True(DifferentPixels(wave, widerWave, centre) > 1000);
            var image = new byte[256 * 160 * 4];
            wave.CopyPixels(image, 256 * 4, 0);
            var sharpPixels = 0;
            for (var y = 45; y < 115; y++)
                for (var x = 50; x < 200; x++)
                {
                    var value = image[(y * 256 + x) * 4];
                    if (value <= 40 || value >= 210) sharpPixels++;
                }
            Assert.True(sharpPixels > 5500, "Water refraction must displace, not blur, the centre texture.");
            Export("crystal-whole-pane-water", wave);
            await Task.CompletedTask;
        });
    }

    private static BitmapSource LensTexture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var value = (byte)((x / 6 + y / 6) % 2 == 0 ? 215 : 35);
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    [Fact]
    public async Task LiveMaterialCompositionKeepsBackgroundVisibleAndContentSeparate()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            foreach (var size in new[] { new System.Windows.Size(280, 220), new System.Windows.Size(380, 300) })
            {
                var width = (int)size.Width;
                var height = (int)size.Height;
                var group = new DeskGroup { Material = "Crystal", Appearance = new MiniC.Models.MaterialPreferences
                    { CrystalSeeThrough = true, Radius = 16 } };
                var scene = new System.Windows.Controls.Grid { Width = width, Height = height, Background = Brushes.White };
                var background = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                for (var i = 0; i < 10; i++)
                    background.Children.Add(new System.Windows.Controls.TextBlock { Text = "Other window    Title    Details    09:30",
                        FontSize = 13, Foreground = i % 2 == 0 ? Brushes.Black : Brushes.RoyalBlue, Margin = new Thickness(8, 3, 0, 3) });
                scene.Children.Add(background);
                scene.Measure(size);
                scene.Arrange(new Rect(size));
                scene.UpdateLayout();
                scene.Children.Add(new CrystalGlassSurface { Radius = 16, UseWallpaper = false,
                    Tint = group.GlassBackground });
                scene.Children.Add(new System.Windows.Controls.TextBlock { Text = "MiniC", FontSize = 14, Foreground = group.PrimaryForeground, Effect = group.ContentShadow,
                    Margin = new Thickness(14, 12, 0, 0), VerticalAlignment = VerticalAlignment.Top });
                var icon = new BitmapImage(new Uri("pack://application:,,,/MiniC;component/icon.ico", UriKind.Absolute));
                for (var i = 0; i < 3; i++)
                {
                    var item = new System.Windows.Controls.StackPanel { Width = 72, Margin = new Thickness(12 + i * 88, 68, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                    item.Children.Add(new System.Windows.Controls.Image { Source = icon, Width = 44, Height = 44 });
                    item.Children.Add(new System.Windows.Controls.TextBlock { Text = "MiniC " + (i + 1), Foreground = group.PrimaryForeground, Effect = group.ContentShadow,
                        FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
                    scene.Children.Add(item);
                }
                scene.Measure(size);
                scene.Arrange(new Rect(size));
                scene.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(scene);
                var centre = Pixel(image, width - 15, height - 30);
                Assert.InRange(centre[1], (byte)245, (byte)255);
                Assert.Equal(255, centre[3]);
                var contrast = Enumerable.Range(0, 4).Max(x => Math.Abs(Pixel(image, x, height - 30)[1] - centre[1]));
                Assert.True(contrast >= 15, $"The narrow contour must be visible over white; contrast={contrast}.");
                Export($"crystal-live-composition-{width}", image);
            }
            await Task.CompletedTask;
        });
    }

    private static RenderTargetBitmap RenderLens(BitmapSource image, double strength, double angle,
        double rippleStrength = 0, double rippleScale = 1, double cornerRadius = 16)
    {
        var radians = angle * Math.PI / 180;
        var surface = new System.Windows.Controls.Border
        {
            Width = image.PixelWidth, Height = image.PixelHeight,
            Background = new ImageBrush(image) { Stretch = Stretch.Fill },
            Effect = new CrystalGlassEffect
            {
                Dimensions = new System.Windows.Point(image.PixelWidth, image.PixelHeight),
                CornerRadius = cornerRadius, Strength = strength, RippleStrength = rippleStrength, RippleScale = rippleScale,
                BevelExtension = Math.Max(0, 32 - cornerRadius),
                Direction = new System.Windows.Point(Math.Cos(radians), Math.Sin(radians))
            }
        };
        surface.Measure(new System.Windows.Size(image.PixelWidth, image.PixelHeight));
        surface.Arrange(new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        surface.UpdateLayout();
        var output = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        output.Render(surface);
        return output;
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static int DifferentPixels(BitmapSource first, BitmapSource second, Int32Rect region)
    {
        var a = new byte[region.Width * region.Height * 4];
        var b = new byte[a.Length];
        first.CopyPixels(region, a, region.Width * 4, 0);
        second.CopyPixels(region, b, region.Width * 4, 0);
        return Enumerable.Range(0, a.Length / 4).Count(i => Math.Abs(a[i * 4 + 1] - b[i * 4 + 1]) > 4);
    }

    private static void Export(string name, BitmapSource image)
    {
        if (Environment.GetEnvironmentVariable("MINIC_RENDER_OUTPUT") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }

    [Fact]
    public async Task WallpaperInvalidationStartsNewLoadWithoutReusingOldTask()
    {
        WallpaperTextureService.Invalidate();
        using var pending = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var oldScene = new WallpaperScene([], Colors.Red, 4);
            var newScene = new WallpaperScene([], Colors.Blue, 4);
            var first = WallpaperTextureService.GetOrLoad(() =>
            {
                pending.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return oldScene;
            });
            Assert.True(pending.Wait(TimeSpan.FromSeconds(5)));
            Assert.Same(first, WallpaperTextureService.GetOrLoad(() => newScene));
            var version = WallpaperTextureService.Version;
            WallpaperTextureService.Invalidate();
            Assert.True(WallpaperTextureService.Version > version);
            var second = WallpaperTextureService.GetOrLoad(() => newScene);
            Assert.NotSame(first, second);
            Assert.Same(newScene, await second);
            release.Set();
            Assert.Same(oldScene, await first);
        }
        finally
        {
            release.Set();
            WallpaperTextureService.Invalidate();
        }
    }

    [Fact]
    public void WallpaperPositioningHandlesFillFitStretchAndNegativeMonitorCoordinates()
    {
        var monitor = new Rect(-1920, 0, 1920, 1080);
        Assert.Equal(monitor, WallpaperTextureService.FitImage(monitor, 1000, 1000, 2));
        Assert.Equal(new Rect(-1920, -420, 1920, 1920), WallpaperTextureService.FitImage(monitor, 1000, 1000, 4));
        Assert.Equal(new Rect(-1500, 0, 1080, 1080), WallpaperTextureService.FitImage(monitor, 1000, 1000, 3));
        Assert.Equal(new Rect(-1460, 40, 1000, 1000), WallpaperTextureService.FitImage(monitor, 1000, 1000, 0));
    }

    [Fact]
    public async Task FrostedWallpaperNeverInheritsCrystalShaderWhenBlurIsZero()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var surface = new WallpaperGlassSurface { RefractionEnabled = false, BlurRadius = 18, UseWallpaper = true };
            Assert.IsType<System.Windows.Media.Effects.BlurEffect>(surface.Effect);
            surface.BlurRadius = 0;
            Assert.Null(surface.Effect);
            surface.RefractionEnabled = true;
            if (RenderCapability.IsPixelShaderVersionSupported(2, 0))
                Assert.IsType<CrystalGlassEffect>(surface.Effect);
            await Task.CompletedTask;
        });
    }

}
