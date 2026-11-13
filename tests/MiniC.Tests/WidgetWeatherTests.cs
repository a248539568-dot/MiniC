using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiniC.Widgets;
using Xunit;

namespace MiniC.Tests;

public sealed class WidgetWeatherTests
{
    [Fact]
    public async Task WeatherTransportConnectsDirectlyEvenWhenAProxyIsAssigned()
    {
        using var handler = WidgetWeatherService.CreateDirectHandler();
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        handler.Proxy = new RejectingProxy();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = Respond();
        using var client = new HttpClient(handler);
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        Assert.Equal("direct", await client.GetStringAsync($"http://127.0.0.1:{port}/weather", timeout.Token));
        await response;

        async Task Respond()
        {
            using var socket = await server.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\ndirect"), timeout.Token);
        }
    }

    private sealed class RejectingProxy : IWebProxy
    {
        public ICredentials Credentials { get; set; }
        public Uri GetProxy(Uri destination) => throw new InvalidOperationException("Weather must not consult a proxy.");
        public bool IsBypassed(Uri host) => throw new InvalidOperationException("Weather must not consult a proxy.");
    }
    [Theory]
    [InlineData("雷阵雨", WeatherSymbol.Thunder)]
    [InlineData("小雪", WeatherSymbol.Snow)]
    [InlineData("多云", WeatherSymbol.PartlyCloudy)]
    [InlineData("雾", WeatherSymbol.Fog)]
    [InlineData("晴", WeatherSymbol.Clear)]
    public void ChineseConditionsMapToVectorSymbols(string name, WeatherSymbol expected) =>
        Assert.Equal(expected, WidgetWeatherService.Classify(name));

    [Fact]
    public async Task ChinaMeteorologicalAdministrationIsPrimary()
    {
        var requests = new List<HttpRequestMessage>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request);
            if (request.RequestUri!.AbsolutePath.Contains("autocomplete")) return CmaSearchJson;
            if (request.RequestUri.AbsolutePath.Contains("weather/view")) return CmaViewJson;
            if (request.RequestUri.AbsolutePath.Contains("hourly")) return CmaHourlyJson;
            throw new InvalidOperationException(request.RequestUri.AbsoluteUri);
        }));
        var service = new WidgetWeatherService(client, cleanupLegacyCredentials: false);

        var weather = await service.GetAsync("贵阳市", new WeatherLocation("贵阳", 26.58, 106.71));

        Assert.Equal("中国气象局", weather.Source);
        Assert.Equal("贵阳", weather.Place);
        Assert.Equal(30.2, weather.Temperature);
        Assert.Equal(30, weather.High);
        Assert.Equal(20, weather.Low);
        Assert.Equal(WeatherSymbol.PartlyCloudy, weather.Symbol);
        Assert.Equal(3, weather.Forecast!.Count);
        Assert.Equal(3, weather.Hourly!.Count);
        Assert.Equal(new TimeOnly(17, 0), weather.Hourly[0].Time);
        Assert.Equal(new DateOnly(2026, 9, 22), weather.Forecast[0].Date);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 15, 15, 0, TimeSpan.FromHours(8)), weather.FetchedAt);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal("weather.cma.cn", request.RequestUri!.Host));
    }

    [Fact]
    public async Task InvalidCmaResponseFallsBackToWttr()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.AbsoluteUri);
            if (uri.Host == "weather.cma.cn") return """{"code":1,"data":[]}""";
            if (uri.Host.Contains("wttr.in")) return WttrJson;
            throw new InvalidOperationException(uri.AbsoluteUri);
        }));
        var service = new WidgetWeatherService(client, cleanupLegacyCredentials: false);

        var weather = await service.GetAsync("贵阳", new WeatherLocation("贵阳", 26.58, 106.71));

        Assert.Equal("wttr.in", weather.Source);
        Assert.Equal(24, weather.Temperature);
        Assert.Equal(3, weather.Forecast!.Count);
        Assert.Equal(8, weather.Hourly!.Count);
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, url => url.Contains("weather.cma.cn"));
        Assert.Contains(requests, url => url.Contains("wttr.in"));
        Assert.DoesNotContain(requests, url => url.Contains("open-meteo.com"));
    }

    [Fact]
    public async Task InvalidCmaAndWttrResponsesFallBackToOpenMeteo()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.AbsoluteUri);
            if (uri.Host == "weather.cma.cn") return """{"code":1,"data":[]}""";
            if (uri.Host.Contains("wttr.in")) return "{}";
            if (uri.Host.Contains("open-meteo.com")) return OpenMeteoJson;
            throw new InvalidOperationException(uri.AbsoluteUri);
        }));
        var service = new WidgetWeatherService(client, cleanupLegacyCredentials: false);

        var weather = await service.GetAsync("杭州", new WeatherLocation("杭州", 30.2741, 120.1551));

        Assert.Equal("Open-Meteo", weather.Source);
        Assert.Equal(20, weather.Temperature);
        Assert.Equal(3, weather.Forecast!.Count);
        Assert.Equal(8, weather.Hourly!.Count);
        Assert.Equal(3, requests.Count);
        Assert.Contains(requests, url => url.Contains("weather.cma.cn"));
        Assert.Contains(requests, url => url.Contains("wttr.in"));
        Assert.Contains(requests, url => url.Contains("api.open-meteo.com"));
    }

    [Fact]
    public async Task IpLocationLocalizesCityAndCmaUsesLocalizedName()
    {
        var urls = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            urls.Add(uri);
            if (uri.Contains("ipwho.is")) return """{"success":true,"city":"Guiyang","latitude":26.58,"longitude":106.71}""";
            if (uri.Contains("geocoding")) return """{"results":[{"name":"贵阳","latitude":26.58,"longitude":106.71}]}""";
            if (uri.Contains("autocomplete")) return CmaSearchJson;
            if (uri.Contains("weather/view")) return CmaViewJson;
            if (uri.Contains("hourly")) return CmaHourlyJson;
            throw new InvalidOperationException(uri);
        }));
        var service = new WidgetWeatherService(client, cleanupLegacyCredentials: false);

        var location = await service.LocateAsync();
        Assert.Equal("贵阳", location!.City);
        Assert.Same(location, await service.LocateAsync());
        var weather = await service.GetAsync(location.City, location);
        Assert.Equal("中国气象局", weather.Source);
        Assert.Equal("贵阳", weather.Place);
        Assert.Equal(3, weather.Forecast!.Count);
        Assert.Equal(3, weather.Hourly!.Count);
        Assert.Single(urls, url => url.Contains("ipwho.is"));
        Assert.Single(urls, url => url.Contains("geocoding"));
        Assert.Single(urls, url => url.Contains("autocomplete"));
    }

    private const string CmaSearchJson = """
        {"msg":"success","code":0,"data":["57816|贵阳|Guiyang|中国","57973|桂阳|Guiyang|中国"]}
        """;

    private const string CmaViewJson = """
        {"msg":"success","code":0,"data":{
          "location":{"id":"57816","name":"贵阳","path":"中国, 贵州, 贵阳","longitude":106.73,"latitude":26.59,"timezone":8},
          "daily":[
            {"date":"2026/09/21","high":30.0,"dayText":"多云","dayCode":1,"low":20.0,"nightText":"多云","nightCode":1},
            {"date":"2026/09/22","high":30.0,"dayText":"多云","dayCode":1,"low":21.0,"nightText":"多云","nightCode":1},
            {"date":"2026/09/23","high":29.0,"dayText":"阵雨","dayCode":3,"low":20.0,"nightText":"小雨","nightCode":7},
            {"date":"2026/09/24","high":28.0,"dayText":"小雨","dayCode":7,"low":19.0,"nightText":"阴","nightCode":2}
          ],
          "now":{"temperature":30.2,"humidity":44.0,"feelst":32.4},
          "lastUpdate":"2026/09/21 15:15"
        }}
        """;

    private const string CmaHourlyJson = """
        {"msg":"success","code":0,"data":[{"date":"2026/09/21","list":[
          {"forecastTime":"2026/09/21 17:00","temperature":30.1,"weather":1,"text":"多云"},
          {"forecastTime":"2026/09/21 20:00","temperature":25.6,"weather":1,"text":"多云"},
          {"forecastTime":"2026/09/21 23:00","temperature":22.8,"weather":1,"text":"多云"},
          {"forecastTime":"2026/09/22 02:00","temperature":20.5,"weather":1,"text":"多云"}
        ]}]}
        """;

    private const string OpenMeteoJson = """
        {
          "current":{"temperature_2m":20,"weather_code":0},
          "hourly":{
            "time":["2026-09-21T00:00","2026-09-21T03:00","2026-09-21T06:00","2026-09-21T09:00","2026-09-21T12:00","2026-09-21T15:00","2026-09-21T18:00","2026-09-21T21:00"],
            "temperature_2m":[18,18,19,22,25,24,21,20],
            "weather_code":[0,0,1,1,2,2,3,3]
          },
          "daily":{"time":["2026-09-21","2026-09-22","2026-09-23","2026-09-24"],"weather_code":[0,1,2,3],"temperature_2m_max":[25,26,25,24],"temperature_2m_min":[15,16,15,14]}
        }
        """;

    private const string WttrJson = """
        {"data":{
          "current_condition":[{"temp_C":"24","weatherCode":"176","weatherDesc":[{"value":"Patchy rain nearby"}]}],
          "weather":[
            {"date":"2026-09-21","maxtempC":"27","mintempC":"18","hourly":[
              {"time":"0","tempC":"18","weatherCode":"113"},{"time":"300","tempC":"18","weatherCode":"113"},
              {"time":"600","tempC":"19","weatherCode":"116"},{"time":"900","tempC":"22","weatherCode":"116"},
              {"time":"1200","tempC":"25","lang_zh":[{"value":"雷阵雨"}]},{"time":"1500","tempC":"24","weatherCode":"176"},
              {"time":"1800","tempC":"21","weatherCode":"119"},{"time":"2100","tempC":"20","weatherCode":"119"}
            ]},
            {"date":"2026-09-22","maxtempC":"28","mintempC":"19","hourly":[{"time":"1200","lang_zh":[{"value":"晴"}]}]},
            {"date":"2026-09-23","maxtempC":"26","mintempC":"18","hourly":[{"time":"1200","lang_zh":[{"value":"多云"}]}]},
            {"date":"2026-09-24","maxtempC":"24","mintempC":"17","hourly":[{"time":"1200","lang_zh":[{"value":"小雨"}]}]}
          ]
        }}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply(request), Encoding.UTF8, "application/json")
            });
    }
}
