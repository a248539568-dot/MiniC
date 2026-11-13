#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniC.Widgets;
using Xunit;
using Image = System.Windows.Controls.Image;

namespace MiniC.Tests;

public sealed class WeatherLayoutTests
{
    private static readonly double[] ResizeWidths = [234, 324, 464, 784, 234];

    [Theory]
    [InlineData(234, 110, "Large")]
    [InlineData(234, 420, "Small")]
    [InlineData(324, 300, "Small")]
    [InlineData(464, 420, "Large")]
    [InlineData(784, 540, "Small")]
    public async Task ForecastReflowsWithoutClippingOrRequestingAgain(double width, double height, string mode)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new WeatherFixture(mode: mode);
            await fixture.LoadAsync();
            var view = fixture.View;
            var hourly = Descendants(view.Body).OfType<WeatherHourlyGrid>().Single();
            Assert.Equal(6, hourly.Children.Count);
            var requests = fixture.Handler.RequestCount;
            var image = Descendants(view.Body).OfType<Image>().First();
            var source = image.Source;
            var temperature = Descendants(view.Body).OfType<TextBlock>().Single(t => t.Text == "-18°" && t.FontSize == 48);
            foreach (var resizeWidth in ResizeWidths)
            {
                Layout(view, resizeWidth, height);
                AssertFits(view);
                Assert.Same(source, image.Source);
                Assert.Contains(temperature, Descendants(view.Body));
            }
            Layout(view, width, height);
            AssertFits(view);
            Assert.Equal(requests, fixture.Handler.RequestCount);
            Assert.True(hourly.Columns >= 1 && hourly.Columns <= 6);
            Assert.All(Descendants(view.Body).OfType<Image>(), icon => Assert.NotNull(icon.Source));
            SavePreview(view, $"weather-{width:0}x{height:0}.png");
            view.Scroll.ScrollToEnd();
            view.UpdateLayout();
            var last = Descendants(view.Body).OfType<TextBlock>().Last();
            Assert.True(last.TranslatePoint(new System.Windows.Point(0, last.ActualHeight), view).Y <= height + 1);
            SavePreview(view, $"weather-{width:0}x{height:0}-end.png");
        });
    }

    [Fact]
    public async Task MissingForecastSectionsCollapseAndSingleHourUsesAvailableWidth()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var empty = new WeatherFixture(hourCount: 0, dayCount: 1);
            await empty.LoadAsync();
            foreach (var width in ResizeWidths)
            {
                Layout(empty.View, width, 300);
                AssertFits(empty.View);
                Assert.DoesNotContain(Descendants(empty.View.Body).OfType<TextBlock>(), t =>
                    t.IsVisible && (t.Text == "未来小时" || t.Text == "未来几天"));
            }
            using var single = new WeatherFixture(hourCount: 1, dayCount: 2);
            await single.LoadAsync();
            foreach (var width in ResizeWidths)
            {
                Layout(single.View, width, 300);
                AssertFits(single.View);
                Assert.Equal(1, Descendants(single.View.Body).OfType<WeatherHourlyGrid>().Single().Columns);
            }
        });
    }

    [Fact]
    public async Task LoadingAndFailureReflowWithoutRestartingInFlightRequests()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new WeatherFixture();
            fixture.Handler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Handler.Fail = true;
            fixture.View.RenderBody();
            var requests = fixture.Handler.RequestCount;
            foreach (var width in ResizeWidths)
            {
                Layout(fixture.View, width, 240);
                AssertFits(fixture.View);
                Assert.Equal(requests, fixture.Handler.RequestCount);
                Assert.Contains(Descendants(fixture.View.Body).OfType<TextBlock>(), t => t.Text == "正在更新天气…");
            }
            fixture.Handler.Gate.SetResult();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (Descendants(fixture.View.Body).OfType<TextBlock>().Any(t => t.Text == "天气暂不可用")) break;
                await Task.Delay(5);
            }
            Assert.Contains(Descendants(fixture.View.Body).OfType<TextBlock>(), t => t.Text == "天气暂不可用");
            var failedRequests = fixture.Handler.RequestCount;
            foreach (var width in ResizeWidths)
            {
                Layout(fixture.View, width, 240);
                AssertFits(fixture.View);
                Assert.Equal(failedRequests, fixture.Handler.RequestCount);
            }
            SavePreview(fixture.View, "weather-error.png");
        });
    }

    private static void Layout(WidgetContentView view, double width, double height)
    {
        view.Measure(new System.Windows.Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    private static void AssertFits(WidgetContentView view)
    {
        Assert.True(view.Scroll.ScrollableWidth <= 0.1);
        foreach (var control in Descendants(view.Body).OfType<FrameworkElement>()
            .Where(e => e is TextBlock or Image && e.IsVisible && e.ActualWidth > 0))
        {
            var bounds = control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= view.ActualWidth + 1,
                $"{control.GetType().Name} clipped at {view.ActualWidth}: {bounds}");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void SavePreview(WidgetContentView view, string name)
    {
        SaveElement(view, name);
        if (view.ActualWidth != 324 || view.ActualHeight != 300) return;
        var background = view.ContentRoot.Background;
        try
        {
            view.ContentRoot.Background = Brushes.White;
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            SaveElement(view, "white-" + name);
            view.ContentRoot.Background = Brushes.Black;
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            SaveElement(view, "black-" + name);
        }
        finally { view.ContentRoot.Background = background; }
    }

    private static void SaveElement(WidgetContentView view, string name)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "weather-responsive");
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name));
        encoder.Save(file);
    }

    private sealed class WeatherFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MiniC-Weather-Layout-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client;
        private readonly WidgetWeatherService _weather;
        internal WeatherHandler Handler { get; }
        internal WidgetContentView View { get; }
        private const string City = "呼伦贝尔市鄂温克族自治旗巴彦托海镇";

        internal WeatherFixture(string mode = "Small", int hourCount = 6, int dayCount = 4)
        {
            Handler = new WeatherHandler(hourCount, dayCount);
            _client = new HttpClient(Handler);
            _weather = new WidgetWeatherService(_client, cleanupLegacyCredentials: false);
            var state = new WidgetState { Kind = WidgetKind.Weather, Title = "天气", City = City, ViewMode = mode };
            var store = new WidgetStore(_root);
            store.Save(state);
            View = new WidgetContentView(state, store, _weather, () => { }, _ => { })
                { Background = new SolidColorBrush(Color.FromRgb(50, 58, 64)) };
        }

        internal async Task LoadAsync()
        {
            await _weather.GetAsync(City);
            View.RenderBody();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        public void Dispose()
        {
            View.Stop();
            _client.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed class WeatherHandler(int hourCount, int dayCount) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }
        internal TaskCompletionSource? Gate { get; set; }
        internal bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (Fail) throw new HttpRequestException("offline layout fixture");
            var path = request.RequestUri!.AbsolutePath;
            var today = DateTime.Today;
            object payload;
            if (path.Contains("autocomplete", StringComparison.Ordinal))
                payload = new { code = 0, data = new[] { "57816|呼伦贝尔|Hulunbuir|中国" } };
            else if (path.Contains("weather/view", StringComparison.Ordinal))
                payload = new { code = 0, data = new {
                    location = new { name = "呼伦贝尔市鄂温克族自治旗巴彦托海镇" },
                    now = new { temperature = -18 },
                    daily = Enumerable.Range(0, dayCount).Select(index => new {
                        date = today.AddDays(index).ToString("yyyy/MM/dd"), high = -10, low = -24,
                        dayText = "多云转小雪，局部地区伴有大风降温" }).ToArray() } };
            else if (path.Contains("hourly", StringComparison.Ordinal))
                payload = new { code = 0, data = new[] { new {
                    list = Enumerable.Range(0, hourCount).Select(index => new {
                        forecastTime = today.AddHours(index * 3).ToString("yyyy/MM/dd HH:mm"),
                        temperature = -18 + index, text = "多云转小雪" }).ToArray() } } };
            else throw new InvalidOperationException("Unexpected layout fixture request: " + request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        }
    }
}
