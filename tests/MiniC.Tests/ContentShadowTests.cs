using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniC.Services;
using MiniC.Widgets;
using Xunit;

namespace MiniC.Tests;

public sealed class ContentShadowTests
{
    [Fact]
    public async Task WhiteForegroundKeepsAVisibleShadowOnWhiteSurface()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var scene = new Grid { Width = 260, Height = 100, Background = Brushes.White };
            var label = new TextBlock
            {
                Text = "MiniC",
                FontSize = 24,
                Foreground = Brushes.White,
                Effect = GroupMaterialBrushes.ContentShadow,
                Margin = new Thickness(32, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            var glyph = new Border
            {
                Width = 30,
                Height = 30,
                Background = Brushes.White,
                Effect = GroupMaterialBrushes.ContentShadow,
                Margin = new Thickness(170, 22, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            scene.Children.Add(label);
            scene.Children.Add(glyph);
            scene.Measure(new System.Windows.Size(260, 100));
            scene.Arrange(new Rect(0, 0, 260, 100));
            scene.UpdateLayout();
            var image = new RenderTargetBitmap(260, 100, 96, 96, PixelFormats.Pbgra32);
            image.Render(scene);

            Assert.True(CountDarkPixels(image, new Int32Rect(24, 10, 85, 55)) >= 12,
                "White text needs a visible shadow on a white background.");
            Assert.True(CountDarkPixels(image, new Int32Rect(160, 12, 55, 55)) >= 12,
                "White icon pixels need a visible shadow on a white background.");
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    public async Task ShadowSoftensForegroundEdgesWithoutFillingOrDistortingItsBackground(bool bright, double dpi)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var background = bright ? Brushes.White : Brushes.Black;
            var scene = new Grid { Width = 300, Height = 120, Background = background };
            var glyph = new Border { Width = 36, Height = 20, Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(40, 32, 0, 0), Effect = GroupMaterialBrushes.ContentShadow };
            scene.Children.Add(glyph);
            var label = WidgetContentControls.Text("待办与日程 12:30", 14);
            label.Margin = new Thickness(90, 30, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Top;
            scene.Children.Add(label);
            scene.Measure(new System.Windows.Size(300, 120));
            scene.Arrange(new Rect(0, 0, 300, 120));
            scene.UpdateLayout();
            var image = new RenderTargetBitmap((int)(300 * dpi), (int)(120 * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            image.Render(scene);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, (int)(50 * dpi), (int)(40 * dpi)));
            if (bright)
            {
                var near = Pixel(image, (int)(40 * dpi) - 1, (int)(40 * dpi))[0];
                var far = Pixel(image, (int)(40 * dpi) - (int)(3 * dpi), (int)(40 * dpi))[0];
                Assert.InRange(near, 130, 250);
                Assert.True(far > near, "A soft shadow must fade away from the foreground instead of outlining it.");
                var below = Pixel(image, (int)(55 * dpi), (int)(54 * dpi))[0];
                var above = Pixel(image, (int)(55 * dpi), (int)(29 * dpi))[0];
                Assert.True(below < above, "The shadow should fall slightly below the foreground.");
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image, (int)(30 * dpi), (int)(40 * dpi)));
            }
            else Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(image, (int)(30 * dpi), (int)(40 * dpi)));
            var directory = Environment.GetEnvironmentVariable("MINIC_RENDER_OUTPUT");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(directory, $"content-shadow-{bright}-{dpi}.png"));
                encoder.Save(stream);
            }
            await Task.CompletedTask;
        });
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var result = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), result, 4, 0);
        return result;
    }

    private static int CountDarkPixels(BitmapSource image, Int32Rect area)
    {
        var count = 0;
        for (var y = area.Y; y < area.Y + area.Height; y++)
            for (var x = area.X; x < area.X + area.Width; x++)
                if (Pixel(image, x, y)[0] < 220) count++;
        return count;
    }
}
